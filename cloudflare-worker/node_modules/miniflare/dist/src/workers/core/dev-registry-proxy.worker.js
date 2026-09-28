// src/workers/core/dev-registry-proxy.worker.ts
import { RpcTarget, WorkerEntrypoint } from "cloudflare:workers";

// src/workers/queues/constants.ts
var HEADER_QUEUE_NAME = "MF-Queue-Name", SERVICE_QUEUE_PREFIX = "queues:queue";
function getQueueServiceName(queueId) {
  return `${SERVICE_QUEUE_PREFIX}:${queueId}`;
}

// src/workers/core/constants.ts
var CorePaths = {
  /** Magic proxy used by getPlatformProxy */
  PLATFORM_PROXY: "/cdn-cgi/local/platform-proxy",
  /** Trigger scheduled event handlers */
  SCHEDULED: "/cdn-cgi/local/scheduled",
  /** Trigger email event handlers */
  EMAIL: "/cdn-cgi/local/email",
  /** Local explorer UI and API */
  EXPLORER: "/cdn-cgi/local/explorer",
  /** Stream video serving endpoint (outside /cdn-cgi/ for tunnel access) */
  STREAM_VIDEO: "/__cf_local/stream",
  /** Local image delivery endpoint (outside /cdn-cgi/ for tunnel access) */
  IMAGE_DELIVERY: "/__cf_local/imagedelivery",
  /** Local Direct Creator Upload completion endpoint (outside /cdn-cgi/ for tunnel access) */
  IMAGE_UPLOAD: "/__cf_local/imageupload",
  /** Public R2 bucket object serving endpoint */
  R2_PUBLIC: "/cdn-cgi/local/r2/public",
  /** S3-compatible API endpoint for local R2 buckets */
  R2_S3: "/cdn-cgi/local/r2/s3"
};

// src/workers/core/dev-registry-proxy-shared.worker.ts
import { DurableObject } from "cloudflare:workers";
var registry = /* @__PURE__ */ new Map();
function setRegistry(data) {
  let entries = Object.entries(data);
  registry = new Map(entries);
}
function resolveTarget(service) {
  let entry = registry.get(service);
  if (!(!entry || !("debugPortAddress" in entry)))
    return entry;
}
function resolveSharedStorageOwner(storageScope) {
  return Array.from(registry.entries()).filter(([, entry]) => entry.storageScope === storageScope).sort(
    ([previousName, previous], [nextName, next]) => previous.created - next.created || previousName.localeCompare(nextName)
  )[0]?.[1];
}
function findQueueConsumer(queueName) {
  for (let entry of registry.values())
    if (Array.isArray(entry.queueConsumers) && entry.queueConsumers.includes(queueName) && entry.debugPortAddress)
      return entry;
}
function hasRegistryEntry(service) {
  return registry.has(service);
}
function workerNotFoundMessage(service) {
  return hasRegistryEntry(service) ? `Worker "${service}" is not compatible with this version of the dev server. Please update all Worker instances to the same version.` : `Worker "${service}" not found. Make sure it is running locally.`;
}
function openDebugPortClient(debugPort, target, selfInstanceId) {
  return selfInstanceId !== void 0 && target.instanceId !== void 0 && target.instanceId === selfInstanceId ? debugPort.current() : debugPort.connect(target.debugPortAddress);
}
function connectToActor(debugPort, scriptName, className, actorId, selfInstanceId) {
  let target = resolveTarget(scriptName);
  return !target || !target.debugPortAddress ? null : openDebugPortClient(debugPort, target, selfInstanceId).getActor(target.userWorkerService, className, actorId);
}
function createProxyDurableObjectClass({
  scriptName,
  className
}) {
  return class extends DurableObject {
    _cachedFetcher;
    _cachedDebugPortAddress;
    _cachedInstanceId;
    // Lazily resolve and cache. Invalidates when the target's debugPortAddress
    // or instanceId changes -- the latter matters because it decides whether we
    // reach the target in-process or over TCP.
    _resolve() {
      let target = resolveTarget(scriptName);
      if (this._cachedFetcher && target?.debugPortAddress === this._cachedDebugPortAddress && target?.instanceId === this._cachedInstanceId)
        return this._cachedFetcher;
      this._cachedFetcher = void 0, this._cachedDebugPortAddress = void 0, this._cachedInstanceId = void 0;
      let fetcher = connectToActor(
        this.env.DEV_REGISTRY_DEBUG_PORT,
        scriptName,
        className,
        this.ctx.id.toString(),
        this.env.DEV_REGISTRY_INSTANCE_ID
      );
      return fetcher && target && (this._cachedFetcher = fetcher, this._cachedDebugPortAddress = target.debugPortAddress, this._cachedInstanceId = target.instanceId), fetcher;
    }
    constructor(ctx, env) {
      return super(ctx, env), new Proxy(this, {
        get(target, prop) {
          if (Reflect.has(target, prop))
            return Reflect.get(target, prop);
          let fetcher = target._resolve();
          return fetcher ? Reflect.get(fetcher, prop) : () => {
            throw new Error(workerNotFoundMessage(scriptName));
          };
        }
      });
    }
    fetch(request) {
      let fetcher = this._resolve();
      return fetcher ? fetcher.fetch(request) : Promise.resolve(
        new Response(workerNotFoundMessage(scriptName), { status: 503 })
      );
    }
  };
}
var SERIALIZED_DATE = "___serialized_date___", SERIALIZED_BIGINT = "___serialized_bigint___";
function tailEventsReplacer(_, value) {
  return value instanceof Date ? { [SERIALIZED_DATE]: value.toISOString() } : typeof value == "bigint" ? { [SERIALIZED_BIGINT]: value.toString() } : value;
}
function tailEventsReviver(_, value) {
  if (value && typeof value == "object") {
    if (SERIALIZED_DATE in value)
      return new Date(value[SERIALIZED_DATE]);
    if (SERIALIZED_BIGINT in value)
      return BigInt(value[SERIALIZED_BIGINT]);
  }
  return value;
}

// src/workers/core/dev-registry-proxy.worker.ts
var HANDLER_RESERVED_KEYS = /* @__PURE__ */ new Set([
  "alarm",
  "connect",
  "self",
  "tail",
  "tailStream",
  "test",
  "trace",
  "webSocketClose",
  "webSocketError",
  "webSocketMessage"
]), ExternalRpcTarget = class extends RpcTarget {
  constructor(resolve2) {
    return super(), new Proxy(this, {
      get(target, prop) {
        return Reflect.has(target, prop) ? Reflect.get(target, prop) : Reflect.get(resolve2(), prop);
      }
    });
  }
};
function getTarget(props) {
  return props.storage ? props.storageScope === void 0 ? void 0 : resolveSharedStorageOwner(props.storageScope) : resolveTarget(props.service);
}
function resolve(props, env, target) {
  let { service, entrypoint, userProps, storage } = props, serviceName = storage ? service : entrypoint === null || entrypoint === "default" ? target.defaultEntrypointService : target.userWorkerService;
  return openDebugPortClient(
    env.DEV_REGISTRY_DEBUG_PORT,
    target,
    env.DEV_REGISTRY_INSTANCE_ID
  ).getEntrypoint(serviceName, entrypoint ?? void 0, userProps);
}
var ExternalQueueProxy = class extends WorkerEntrypoint {
  fetch(request) {
    let queueName = request.headers.get(HEADER_QUEUE_NAME);
    if (queueName === null)
      return new Response(`Missing "${HEADER_QUEUE_NAME}" header`, {
        status: 400
      });
    let target = findQueueConsumer(queueName);
    if (target === void 0)
      return new Response(
        `No Worker consuming queue "${queueName}" found in the local dev registry. Make sure the consumer Worker is running locally.`,
        { status: 503 }
      );
    let broker = openDebugPortClient(
      this.env.DEV_REGISTRY_DEBUG_PORT,
      target,
      this.env.DEV_REGISTRY_INSTANCE_ID
    ).getEntrypoint(getQueueServiceName(queueName)), headers = new Headers(request.headers);
    return headers.delete(HEADER_QUEUE_NAME), broker.fetch(new Request(request, { headers }));
  }
}, ExternalServiceProxy = class extends WorkerEntrypoint {
  _fetcher;
  _targetAddress;
  _targetInstanceId;
  _entryFetcher = null;
  constructor(ctx, env) {
    super(ctx, env);
    let target = resolveTarget(ctx.props.service);
    if (target && target.debugPortAddress) {
      let client = openDebugPortClient(
        env.DEV_REGISTRY_DEBUG_PORT,
        target,
        env.DEV_REGISTRY_INSTANCE_ID
      );
      this._entryFetcher = client.getEntrypoint("core:entry");
    }
    return new Proxy(this, {
      get(target2, prop) {
        if (Reflect.has(target2, prop))
          return Reflect.get(target2, prop);
        if (typeof prop == "string" && HANDLER_RESERVED_KEYS.has(prop))
          return;
        if (typeof prop == "string" && ctx.props.rpcProperties?.includes(prop))
          return new ExternalRpcTarget(() => {
            let fetcher2 = target2._resolve();
            if (!fetcher2)
              throw new Error(workerNotFoundMessage(ctx.props.service));
            return Reflect.get(fetcher2, prop);
          });
        let fetcher = target2._resolve();
        if (!fetcher)
          throw new Error(workerNotFoundMessage(ctx.props.service));
        return Reflect.get(fetcher, prop);
      }
    });
  }
  _resolve() {
    let target = getTarget(this.ctx.props);
    return target === void 0 || !target.debugPortAddress ? (this._fetcher = void 0, this._targetAddress = void 0, this._targetInstanceId = void 0, null) : this._fetcher !== void 0 && this._targetAddress === target.debugPortAddress && this._targetInstanceId === target.instanceId ? this._fetcher : (this._fetcher = resolve(this.ctx.props, this.env, target), this._targetAddress = target.debugPortAddress, this._targetInstanceId = target.instanceId, this._fetcher);
  }
  fetch(request) {
    let fetcher = this._resolve();
    return fetcher ? fetcher.fetch(request) : new Response(workerNotFoundMessage(this.ctx.props.service), {
      status: 503
    });
  }
  async scheduled(controller) {
    if (!this._entryFetcher)
      throw new Error(workerNotFoundMessage(this.ctx.props.service));
    let params = new URLSearchParams();
    controller.cron && params.set("cron", controller.cron), controller.scheduledTime && params.set("time", String(controller.scheduledTime));
    let response = await this._entryFetcher.fetch(
      new Request(`http://localhost${CorePaths.SCHEDULED}?${params}`, {
        headers: { "MF-Route-Override": this.ctx.props.service }
      })
    );
    if (!response.ok) {
      let body = await response.text();
      throw new Error(
        `Scheduled handler returned HTTP ${response.status}: ${body}`
      );
    }
  }
  // Forward tail events to the remote worker via RPC.
  // Events with rpcMethod==="tail" are filtered out to prevent infinite
  // recursion (the remote tail() call would itself produce a tail event).
  async tail(events) {
    let fetcher = this._resolve();
    if (fetcher === null)
      return;
    let filtered = events.filter(
      (e) => e.event?.rpcMethod !== "tail"
    );
    if (filtered.length !== 0)
      try {
        let serializedEvents = JSON.parse(
          JSON.stringify(filtered, tailEventsReplacer),
          tailEventsReviver
        );
        await fetcher.tail(serializedEvents);
      } catch (e) {
        console.warn(
          `[dev-registry] Failed to forward tail events to "${this.ctx.props.service}": ${e instanceof Error ? e.message : String(e)}`
        );
      }
  }
};
export {
  ExternalQueueProxy,
  ExternalServiceProxy,
  createProxyDurableObjectClass,
  setRegistry
};
//# sourceMappingURL=dev-registry-proxy.worker.js.map
