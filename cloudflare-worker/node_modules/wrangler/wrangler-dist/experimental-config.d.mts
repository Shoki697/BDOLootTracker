import { Pipeline, PipelineRecord } from "cloudflare:pipelines";

//#region ../config/dist/public-B4i-aaaO.d.mts

//#region src/inference.d.ts
/**
 * The Worker's entry module, imported with the `{ type: "cf-worker" }` import attribute
 * @example
 * ```ts
 * import * as entrypoint from "./src" with { type: "cf-worker" };
 * ```
 */
type WorkerModule = Record<string, any>;
/**
 * Default module type representing an unknown Worker's exports.
 * - default export can be `ExportedHandler` or a `WorkerEntrypoint` class constructor
 * - named exports can be `WorkerEntrypoint`, `DurableObject`, or `WorkflowEntrypoint` class constructors
 */
interface DefaultModule {
  default?: ExportedHandler | Constructor<Rpc.WorkerEntrypointBranded>;
  [key: string]: ExportedHandler | Constructor<Rpc.WorkerEntrypointBranded> | Constructor<Rpc.DurableObjectBranded> | Constructor<Rpc.WorkflowEntrypointBranded> | undefined;
}
/**
 * Represents a class constructor that creates instances of TInstance.
 */
type Constructor<TInstance> = new (...args: any[]) => TInstance;
/**
 * Extracts the instance type from a class constructor if it extends `TInstance`.
 */
type ExtractInstance<T$1, TInstance> = T$1 extends Constructor<TInstance> ? InstanceType<T$1> : never;
/**
 * Mapping from binding type literals to Cloudflare runtime types.
 *
 * Entries fall into two groups:
 * - Parameterized bindings (ai, json, kv, pipeline, queue, text) refine their
 *   runtime type from the binding instance via nominal matches against the
 *   `Typed*Binding` / `JsonBinding` / `TextBinding` interfaces from
 *   `./bindings`. When `TBinding` does not match, the entry falls back to the
 *   unparameterized runtime type.
 * - Non-parameterized bindings map their type literal directly to a runtime
 *   type and ignore `TBinding`.
 *
 * IMPORTANT: The right-hand-side identifiers in this map (e.g. `KVNamespace`,
 * `ImagesBinding`, `Fetcher`) must resolve to the ambient runtime types from
 * `@cloudflare/workers-types`, not to local config interfaces. Several local
 * binding interfaces in `./bindings.ts` (`ImagesBinding`, `MediaBinding`,
 * `StreamBinding`) share names with ambient globals — importing those local
 * types into this file silently shadows the globals and breaks `InferEnv`.
 * Only import the `Typed*Binding`, `JsonBinding`, and `TextBinding` interfaces
 * from `./bindings` (their names do not collide with ambient globals); never
 * widen the import to a wildcard or to the plain `*Binding` interfaces.
 */
interface BindingTypeMap<TBinding> {
  ai: TBinding extends TypedAiBinding<infer T> ? Ai<T> : Ai;
  json: TBinding extends JsonBinding<infer T> ? T : never;
  kv: TBinding extends TypedKvBinding<infer T> ? KVNamespace<T> : KVNamespace;
  pipeline: TBinding extends TypedPipelineBinding<infer T> ? Pipeline<T> : Pipeline;
  queue: TBinding extends TypedQueueBinding<infer T> ? Queue<T> : Queue;
  text: TBinding extends TextBinding<infer T> ? T : never;
  "agent-memory": AgentMemoryNamespace;
  "ai-search": AiSearchInstance;
  "ai-search-namespace": AiSearchNamespace;
  "analytics-engine-dataset": AnalyticsEngineDataset;
  artifacts: Artifacts;
  assets: Fetcher;
  browser: BrowserRun;
  d1: D1Database;
  "dispatch-namespace": DispatchNamespace;
  "durable-object": DurableObjectNamespace;
  flagship: Flagship;
  hyperdrive: Hyperdrive;
  images: ImagesBinding;
  logfwdr: any;
  media: MediaBinding;
  "mtls-certificate": Fetcher;
  "rate-limit": RateLimit;
  r2: R2Bucket;
  secret: string;
  "secrets-store-secret": SecretsStoreSecret;
  "send-email": SendEmail;
  stream: StreamBinding;
  vectorize: VectorizeIndex;
  "version-metadata": WorkerVersionMetadata;
  "vpc-service": Fetcher;
  "vpc-network": Fetcher;
  worker: Fetcher;
  "worker-loader": WorkerLoader;
  workflow: Workflow;
}
type SelectedWorkerExportName<TBinding> = TBinding extends {
  exportName?: infer TExportName;
} ? TExportName extends string ? TExportName : "default" : "default";
type InferBindingType<TBinding> = TBinding extends {
  type: "worker";
  worker: infer TWorker extends WorkerReference;
} ? TWorker extends string ? Fetcher : InferMainModule<UnwrapConfig<TWorker>> extends infer TModule extends WorkerModule ? SelectedWorkerExportName<TBinding> extends infer TExportName ? TExportName extends keyof TModule ? TModule[TExportName] extends Constructor<any> ? Fetcher<ExtractInstance<TModule[TExportName], Rpc.WorkerEntrypointBranded>> : Fetcher : never : never : never : TBinding extends {
  type: "durable-object";
  worker: infer TWorker extends WorkerReference;
  exportName: infer TExportName extends string;
} ? TWorker extends string ? DurableObjectNamespace : InferMainModule<UnwrapConfig<TWorker>> extends infer TModule extends WorkerModule ? TExportName extends keyof TModule ? DurableObjectNamespace<ExtractInstance<TModule[TExportName], Rpc.DurableObjectBranded>> : never : never : TBinding extends {
  type: "workflow";
  worker: infer TWorker extends WorkerReference;
  exportName: infer TExportName extends string;
} ? TWorker extends string ? Workflow : InferMainModule<UnwrapConfig<TWorker>> extends infer TModule extends WorkerModule ? TExportName extends keyof TModule ? ExtractInstance<TModule[TExportName], Rpc.WorkflowEntrypointBranded> extends infer TWorkflow ? TWorkflow extends {
  run(event: {
    payload: infer P;
  }, step: any): any;
} ? Workflow<P> : Workflow : Workflow : never : never : TBinding extends {
  type: `unsafe:${string}`;
} ? any : TBinding extends {
  type: infer K extends keyof BindingTypeMap<TBinding>;
} ? BindingTypeMap<TBinding>[K] : never;
/**
 * Infer export names from a config's exports, optionally filtered by type.
 * When TExportType is `string` (default), returns all export names.
 * When TExportType is a specific literal like `"durable-object"` or `"workflow"`,
 * returns only exports of that type.
 */
type InferExportsByType<TUnwrappedConfig, TExportType extends string = string> = TUnwrappedConfig extends {
  exports: infer TExports extends Record<string, {
    type: string;
  }>;
} ? { [K in keyof TExports]: TExports[K] extends {
  type: TExportType;
} ? K & string : never }[keyof TExports] : never;
/**
 * Infer `WorkerEntrypoint` export names from a config.
 * Returns named module exports that are not declared as type `"durable-object"` or `"workflow"` in `exports`.
 * Excludes `"default"` since `exportName` should only be provided for named exports.
 */
type InferWorkerEntrypointExports<TUnwrappedConfig> = Exclude<keyof InferMainModule<TUnwrappedConfig> & string, "default" | InferExportsByType<TUnwrappedConfig, "durable-object" | "workflow">>;
/**
 * Unwrap function and promise types to get the underlying config.
 * Use this to normalize a config before passing it to other inference utilities.
 */
type UnwrapConfig<TConfig$1> = TConfig$1 extends ((...args: any[]) => infer TReturn) ? UnwrapConfig<TReturn> : TConfig$1 extends Promise<infer TCompletion> ? UnwrapConfig<TCompletion> : TConfig$1;
/**
 * Infer the `Env` interface type from a Worker config.
 *
 * Transforms a config object's `env` bindings into their
 * corresponding Cloudflare runtime types.
 *
 * @example
 * ```typescript
 * import { defineWorker, bindings } from "@cloudflare/config";
 * import type { InferEnv, UnwrapConfig } from "@cloudflare/config";
 *
 * const config = defineWorker({
 *   env: {
 *     MY_JSON: bindings.json({ id: string }),
 *     MY_KV: bindings.kv(),
 *   },
 * });
 *
 * type WorkerConfig = UnwrapConfig<typeof config>;
 * // Inferred as: { MY_JSON: { id: string }; MY_KV: KVNamespace }
 * export type Env = InferEnv<WorkerConfig>;
 * ```
 */
type InferEnv<TUnwrappedConfig> = TUnwrappedConfig extends {
  env: infer TEnv extends Record<string, any>;
} ? { [K in keyof TEnv]: InferBindingType<TEnv[K]> } : never;
/**
 * Infer the Durable Object namespace names from a Worker config's exports.
 * Returns a union of export names that declare a *live* Durable Object —
 * `type: "durable-object"` with `state` either omitted, `"created"`, or
 * `"expecting-transfer"`. Tombstone entries (`deleted`, `renamed`,
 * `transferred`) are excluded because they retire the namespace and a
 * binding to a tombstoned class would fail at deploy time.
 *
 * @example
 * ```typescript
 * import { defineWorker } from "@cloudflare/config";
 * import type { InferDurableNamespaces, UnwrapConfig } from "@cloudflare/config";
 *
 * const config = defineWorker({
 *   exports: {
 *     MyDurableObject: { type: "durable-object", storage: "sqlite" },
 *     OldGone:         { type: "durable-object", state: "deleted" },
 *   },
 * });
 *
 * type WorkerConfig = UnwrapConfig<typeof config>;
 * // Inferred as: "MyDurableObject" (the deleted tombstone is excluded)
 * type DurableNamespaces = InferDurableNamespaces<WorkerConfig>;
 * ```
 */
type InferDurableNamespaces<TUnwrappedConfig> = TUnwrappedConfig extends {
  exports: infer TExports extends Record<string, {
    type: string;
  }>;
} ? { [K in keyof TExports]: TExports[K] extends {
  type: "durable-object";
} ? TExports[K] extends {
  state: "deleted" | "renamed" | "transferred";
} ? never : K & string : never }[keyof TExports] : never;
/**
 * Infer the main module type from a Worker config's entrypoint.
 * If entrypoint is a module namespace object, returns that type.
 * If entrypoint is a `string` or not present, returns `DefaultModule` as a fallback.
 */
type InferMainModule<TUnwrappedConfig> = TUnwrappedConfig extends {
  entrypoint: infer TModule extends WorkerModule;
} ? TModule : DefaultModule;
//#endregion
//#region src/utils.d.ts
/**
 * Represents any valid JSON value.
 */
type Json = string | number | boolean | null | Json[] | {
  [key: string]: Json;
};
//#endregion
//#region src/definition.d.ts
interface ConfigContext {
  /**
   * The mode the config is being evaluated in.
   * Set via the `--mode` CLI flag.
   * In Vite the mode defaults to `development` in `vite dev` and `production` in `vite build` ([more info](https://vite.dev/guide/env-and-mode.html#modes)).
   * In Wrangler the mode defaults to `undefined`.
   */
  mode: string | undefined;
}
/**
 * The authored config in any of its supported shapes: a plain value, a promise,
 * or a function of {@link ConfigContext}.
 */
type ConfigInput<T$1> = T$1 | Promise<T$1> | ((ctx: ConfigContext) => T$1 | Promise<T$1>);
type ConfigObject = Record<string, unknown>;
type ConfigWithType<T$1 extends ConfigObject, TType extends string> = T$1 & {
  type: TType;
};
//#endregion
//#region src/container-definition.d.ts
type ContainerConfigExport<T$1 extends ContainerConfig = ContainerConfig> = ConfigInput<T$1>;
type DistributiveOmit<T$1, K$1 extends PropertyKey> = T$1 extends unknown ? Omit<T$1, K$1> : never;
/**
 * Authored Container config shape — {@link ContainerConfig} without the `type`
 * discriminant, which `defineContainer` injects.
 */
type ContainerConfigInput = DistributiveOmit<ContainerConfig, "type">;
/** Declare a Container application. */
declare const defineContainer: <const TInput extends ConfigInput<ContainerConfigInput>>(config: TInput) => TInput extends ((ctx: ConfigContext) => infer TResult) ? (ctx: ConfigContext) => TResult extends Promise<infer TConfig extends {
  [x: string]: unknown;
}> ? Promise<ConfigWithType<TConfig, "container">> : TResult extends {
  [x: string]: unknown;
} ? ConfigWithType<TResult, "container"> : never : TInput extends Promise<infer TConfig extends {
  [x: string]: unknown;
}> ? Promise<ConfigWithType<TConfig, "container">> : TInput extends {
  [x: string]: unknown;
} ? ConfigWithType<TInput, "container"> : never;
//#endregion
//#region src/exports.d.ts
/**
 * Storage backend for the Durable Object.
 *
 * Containers are only supported on the SQLite storage engine, so `container` is
 * only offered alongside `storage: "sqlite"`.
 */
type DurableObjectStorageOptions<TContainer extends ContainerConfigExport | undefined = ContainerConfigExport | undefined> = {
  /**
   * Selects the SQLite-backed storage engine (recommended for new
   * classes).
   */
  storage: "sqlite";
  /**
   * Attach a Container application to this Durable Object by config reference.
   */
  container?: TContainer;
} | {
  /** Selects the legacy key-value storage engine. */
  storage: "legacy-kv";
};
/**
 * Declares a provisioned Durable Object class exported from this Worker.
 *
 * For more information about Durable Objects, see the documentation at
 * https://developers.cloudflare.com/workers/learning/using-durable-objects
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#durable-objects
 */
type DurableObjectCreatedExportOptions<TContainer extends ContainerConfigExport | undefined = ContainerConfigExport | undefined> = {
  state?: "created";
} & DurableObjectStorageOptions<TContainer>;
/**
 * Retire a provisioned Durable Object namespace whose class has
 * been removed from code.
 *
 * During deploy, the class must not be exported in the uploaded code, and no
 * other Worker may hold a `durableObject` binding to the namespace.
 */
interface DurableObjectDeletedExportOptions {
  state: "deleted";
}
/**
 * Rename a provisioned Durable Object namespace's class. The
 * `renamedTo` value must also appear as a live (`state: "created"`)
 * `durableObject` entry in the same `exports` map.
 */
interface DurableObjectRenamedExportOptions {
  state: "renamed";
  /**
   * The destination class name. Must be a valid JavaScript identifier and
   * must appear as a live (`state: "created"`) `durableObject` entry in the
   * same `exports` map.
   */
  renamedTo: string;
}
/**
 * Transfer ownership of a Durable Object namespace to another Worker in the same account.
 * The target Worker must first deploy an `expectingTransfer` entry naming this Worker via `transferFrom`.
 */
interface DurableObjectTransferredExportOptions {
  state: "transferred";
  /**
   * The destination Worker. Must reference a Worker in the same account.
   */
  transferredTo: string;
}
/**
 * Prepare to receive cross-Worker Durable Object transfer.
 * Once the source Worker's `transferred` export is deployed, this entry becomes a normal live `durable-object` export.
 */
type DurableObjectExpectingTransferExportOptions<TContainer extends ContainerConfigExport | undefined = ContainerConfigExport | undefined> = {
  state: "expecting-transfer";
  /**
   * The source Worker for the two-phase cross-Worker transfer.
   */
  transferFrom: string;
} & DurableObjectStorageOptions<TContainer>;
type DurableObjectCreatedExport<TContainer extends ContainerConfigExport | undefined = ContainerConfigExport | undefined> = DurableObjectCreatedExportOptions<TContainer> & {
  type: "durable-object";
};
interface DurableObjectDeletedExport extends DurableObjectDeletedExportOptions {
  type: "durable-object";
}
interface DurableObjectRenamedExport extends DurableObjectRenamedExportOptions {
  type: "durable-object";
}
interface DurableObjectTransferredExport extends DurableObjectTransferredExportOptions {
  type: "durable-object";
}
type DurableObjectExpectingTransferExport<TContainer extends ContainerConfigExport | undefined = ContainerConfigExport | undefined> = DurableObjectExpectingTransferExportOptions<TContainer> & {
  type: "durable-object";
};
type DurableObjectExportOptions = DurableObjectCreatedExportOptions | DurableObjectDeletedExportOptions | DurableObjectRenamedExportOptions | DurableObjectTransferredExportOptions | DurableObjectExpectingTransferExportOptions;
type DurableObjectExports = DurableObjectCreatedExport | DurableObjectDeletedExport | DurableObjectRenamedExport | DurableObjectTransferredExport | DurableObjectExpectingTransferExport;
interface WorkerEntrypointExportOptions {
  cache?: {
    /** Whether cache is enabled for this entrypoint. */
    enabled: boolean;
  };
}
interface WorkerEntrypointExport extends WorkerEntrypointExportOptions {
  type: "worker";
}
/**
 * Configuration for named exports declared by the Worker. Each entry's
 * key is the exported class name; the value configures the export.
 */
interface Exports {
  /**
   * Declares a Durable Object class defined by this Worker.
   *
   * For more information about Durable Objects, see the documentation at
   * https://developers.cloudflare.com/workers/learning/using-durable-objects
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#durable-objects
   */
  durableObject<TContainer extends ContainerConfigExport | undefined = undefined>(options: DurableObjectCreatedExportOptions<TContainer>): DurableObjectCreatedExport<TContainer>;
  /**
   * Retire a provisioned Durable Object namespace whose class has been removed from code.
   */
  durableObject(options: DurableObjectDeletedExportOptions): DurableObjectDeletedExport;
  /**
   * Rename a provisioned Durable Object namespace's class.
   */
  durableObject(options: DurableObjectRenamedExportOptions): DurableObjectRenamedExport;
  /**
   * Transfer ownership of a Durable Object namespace to another Worker in the same account.
   */
  durableObject(options: DurableObjectTransferredExportOptions): DurableObjectTransferredExport;
  /**
   * Prepare to receive cross-Worker Durable Object transfer.
   * The source Worker must follow up with a deployment containing a `transferred` export to commit the transfer.
   */
  durableObject<TContainer extends ContainerConfigExport | undefined = undefined>(options: DurableObjectExpectingTransferExportOptions<TContainer>): DurableObjectExpectingTransferExport<TContainer>;
  durableObject(options: DurableObjectExportOptions): DurableObjectExports;
  /** Declares a WorkerEntrypoint export defined by this Worker. */
  worker(options?: WorkerEntrypointExportOptions): WorkerEntrypointExport;
}
/**
 * Exports builder for configuring Worker exports.
 *
 * @example
 * ```typescript
 * import { defineContainer, defineWorker, exports } from "@cloudflare/config";
 *
 * const myContainer = defineContainer({
 *   name: "my-container",
 *   image: { dockerfile: "./Dockerfile" },
 * });
 *
 * export default defineWorker({
 *   exports: {
 *     MyDurableObject: exports.durableObject({ storage: "sqlite" }),
 *     MyContainerDO:   exports.durableObject({ storage: "sqlite", container: myContainer }),
 *     OldClass:        exports.durableObject({ state: "deleted" }),
 *     OldName:         exports.durableObject({ state: "renamed", renamedTo: "NewName" }),
 *     Outgoing:        exports.durableObject({ state: "transferred", transferredTo: "target-worker" }),
 *     Incoming:        exports.durableObject({ state: "expecting-transfer", storage: "sqlite", transferFrom: "source-worker" }),
 *   },
 * });
 * ```
 */
declare const exports: Exports;
//#endregion
//#region src/triggers.d.ts
interface FetchTriggerOptions {
  /**
   * A route that your Worker should be published to.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#types-of-routes
   */
  pattern: string;
  /**
   * The DNS zone the pattern is attached to. Required when the
   * pattern is ambiguous.
   */
  zone?: string;
}
/**
 * Fetch trigger — a route that your Worker should be published to.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#types-of-routes
 */
interface FetchTrigger extends FetchTriggerOptions {
  type: "fetch";
}
interface QueueConsumerTriggerOptions {
  /** The name of the queue from which this consumer should consume. */
  name: string;
  /** The queue to send messages that failed to be consumed. */
  deadLetterQueue?: string;
  /** The maximum number of messages per batch. */
  maxBatchSize?: number;
  /** The maximum number of seconds to wait to fill a batch with messages. */
  maxBatchTimeout?: number;
  /**
   * The maximum number of concurrent consumer Worker invocations.
   * Leaving this unset will allow your consumer to scale to the
   * maximum concurrency needed to keep up with the message backlog.
   */
  maxConcurrency?: number | null;
  /** The maximum number of retries for each message. */
  maxRetries?: number;
  /** The number of seconds to wait before retrying a message. */
  retryDelay?: number;
  /** The number of milliseconds to wait for pulled messages to become visible again. */
  visibilityTimeoutMs?: number;
}
/**
 * Queue consumer trigger — invokes this Worker when messages arrive on the
 * named queue.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#queues
 */
interface QueueConsumerTrigger extends QueueConsumerTriggerOptions {
  type: "queue";
}
interface ScheduledTriggerOptions {
  /**
   * A "cron" definition to trigger a Worker's "scheduled" function.
   *
   * Lets you call Workers periodically, much like a cron job.
   *
   * More details here https://developers.cloudflare.com/workers/platform/cron-triggers
   */
  schedule: string;
}
/**
 * Scheduled (cron) trigger — invokes this Worker on the given schedules.
 *
 * More details here https://developers.cloudflare.com/workers/platform/cron-triggers
 */
interface ScheduledTrigger extends ScheduledTriggerOptions {
  type: "scheduled";
}
interface EmailTriggerOptions {
  /**
   * Inbound Email Routing addresses handled by this Worker.
   *
   * Each entry is a literal recipient address (e.g. `"support@example.com"`)
   * or a `*@domain` catch-all (e.g. `"*@example.com"`).
   */
  addresses: string[];
}
/**
 * Email trigger — invokes this Worker for the configured Email Routing
 * addresses.
 */
interface EmailTrigger extends EmailTriggerOptions {
  type: "email";
}
interface ConnectTriggerOptions {
  /** The transport protocol to listen for. */
  protocol: "tcp";
  /** The port to listen on. */
  port: number;
  /** The address to bind to. Defaults to `127.0.0.1`. */
  address?: string;
}
/**
 * Connect trigger — invokes this Worker's `connect(socket, env, ctx)`
 * handler for raw socket connections received on the configured
 * protocol/port.
 */
interface ConnectTrigger extends ConnectTriggerOptions {
  type: "connect";
}
/**
 * Event triggers — fetch routes, queue consumers, cron schedules, Email
 * Routing addresses, and raw sockets — that invoke this Worker.
 * Construct entries with `triggers.fetch(...)`, `triggers.queue(...)`,
 * `triggers.scheduled(...)`, `triggers.email(...)`, or `triggers.connect(...)`.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#triggers
 */
interface Triggers {
  /**
   * Fetch trigger — a route that your Worker should be published to.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#types-of-routes
   */
  fetch(options: FetchTriggerOptions): FetchTrigger;
  /**
   * Queue consumer trigger — invokes this Worker when messages arrive on the
   * named queue.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#queues
   */
  queue(options: QueueConsumerTriggerOptions): QueueConsumerTrigger;
  /**
   * Scheduled (cron) trigger — invokes this Worker on the given schedules.
   *
   * More details here https://developers.cloudflare.com/workers/platform/cron-triggers
   */
  scheduled(options: ScheduledTriggerOptions): ScheduledTrigger;
  /**
   * Email trigger — invokes this Worker for the configured Email Routing
   * addresses.
   */
  email(options: EmailTriggerOptions): EmailTrigger;
  /**
   * Connect trigger — invokes this Worker's `connect(socket, env, ctx)`
   * handler for raw socket connections received on the configured
   * protocol/port.
   */
  connect(options: ConnectTriggerOptions): ConnectTrigger;
}
/**
 * Triggers builder for configuring event triggers.
 *
 * @example
 * ```typescript
 * import { defineWorker, triggers } from "@cloudflare/config";
 *
 * export default defineWorker({
 *   triggers: [
 *     triggers.fetch({ pattern: "example.com/*", zone: "example.com" }),
 *     triggers.queue({ name: "my-queue" }),
 *     triggers.scheduled({ schedule: "0 * * * *" }),
 *     triggers.scheduled({ schedule: "30 0 * * *" }),
 *     triggers.email({ addresses: ["support@example.com"] }),
 *     triggers.connect({ protocol: "tcp", port: 5432 }),
 *   ],
 * });
 * ```
 */
declare const triggers: Triggers;
//#endregion
//#region src/types.d.ts
/**
 * Union of all binding definitions accepted in `env`.
 */
type Binding = AgentMemoryBinding | AiBinding | AiSearchBinding | AiSearchNamespaceBinding | AnalyticsEngineDatasetBinding | ArtifactsBinding | AssetsBinding | BrowserBinding | D1Binding | DispatchNamespaceBinding | DurableObjectBinding | FlagshipBinding | HyperdriveBinding | ImagesBinding$1 | JsonBinding | KvBinding | LogfwdrBinding | MediaBinding$1 | MtlsCertificateBinding | PipelineBinding | QueueBinding | R2Binding | RateLimitBinding | SecretBinding | SecretsStoreSecretBinding | SendEmailBinding | StreamBinding$1 | TextBinding | UnsafeBinding | VectorizeBinding | VersionMetadataBinding | VpcNetworkBinding | VpcServiceBinding | WorkerBinding | WorkerLoaderBinding;
/**
 * Union of all trigger definitions accepted in `triggers`.
 */
type Trigger = ConnectTrigger | EmailTrigger | FetchTrigger | QueueConsumerTrigger | ScheduledTrigger;
/**
 * Union of all export definitions accepted in `exports`. Worker entries
 * configure WorkerEntrypoint exports. Durable Object entries configure live
 * classes and tombstone lifecycle operations.
 */
type Export = DurableObjectCreatedExport | DurableObjectDeletedExport | DurableObjectRenamedExport | DurableObjectTransferredExport | DurableObjectExpectingTransferExport | WorkerEntrypointExport;
/** An image source accepted in an authored Container configuration. */
type ContainerImage = {
  /** The path to a Dockerfile. */
  dockerfile: string;
  /**
   * Build context of the application.
   *
   * @default The directory containing `dockerfile`.
   */
  buildContext?: string;
  /** Image variables available to the image at build time only. */
  buildVars?: Record<string, string>;
} | {
  /**
   * Reference to an existing image.
   *
   * For supported registries, refer to
   * https://developers.cloudflare.com/containers/guides/image-management/#use-pre-built-container-images
   */
  reference: string;
};
/** Fields shared by all Container application configurations. */
interface BaseContainerConfig {
  /**
   * Discriminates this config as a Container config.
   *
   * Injected automatically by `defineContainer`; only needs to be written by
   * hand when authoring a raw config object without the helper.
   */
  type: "container";
  /**
   * Name of the application.
   *
   * This is also the identifier used to reference the Container from a Durable
   * Object's `exports` entry via its `container` field.
   */
  name: string;
  /** Configures observability for Container instances. */
  observability?: {
    /** Whether observability is enabled. */
    enabled?: boolean;
    logs?: {
      /** Whether log collection is enabled. */
      enabled?: boolean;
    };
  } & ({
    /** Percentage of Container instances targeted for observability. */
    targetInstancePercentage?: number;
    targetInstanceCount?: never;
  } | {
    targetInstancePercentage?: never;
    /** Number of Container instances targeted for observability. */
    targetInstanceCount?: number;
  });
  /**
   * Passed through without client-side validation or transformation.
   *
   * @hidden
   */
  unsafe?: Record<string, unknown>;
}
/** A Container application managed with a standard scheduling policy. */
interface StandardContainerConfig extends BaseContainerConfig {
  /** The image to build or deploy. */
  image: ContainerImage;
  /**
   * Maximum number of application instances.
   *
   * @default 20
   */
  maxInstances?: number;
  /**
   * The instance type to be used for the Container.
   * Select from one of the following named instance types:
   *
   * - lite: 1/16 vCPU, 256 MiB memory, and 2 GB disk
   * - basic: 1/4 vCPU, 1 GiB memory, and 4 GB disk
   * - standard-1: 1/2 vCPU, 4 GiB memory, and 8 GB disk
   * - standard-2: 1 vCPU, 6 GiB memory, and 12 GB disk
   * - standard-3: 2 vCPU, 8 GiB memory, and 16 GB disk
   * - standard-4: 4 vCPU, 12 GiB memory, and 20 GB disk
   *
   * Customers on an enterprise plan have the additional option to set custom
   * limits.
   *
   * @default "lite"
   */
  instanceType?: "basic" | "lite" | "standard-1" | "standard-2" | "standard-3" | "standard-4" | {
    /** @default 0.0625 (1/16 vCPU) */
    vcpu?: number;
    /** @default 256 MiB */
    memoryMib?: number;
    /** @default 2 GB */
    diskMb?: number;
  };
  /**
   * The scheduling policy of the application.
   *
   * @default "default"
   */
  schedulingPolicy?: "default" | "regional";
  ssh?: {
    /**
     * If enabled, users with write access to the Container application can
     * connect to it over SSH.
     *
     * @default false
     */
    enabled: boolean;
    /**
     * Port that the SSH service is running on.
     *
     * @default 22
     */
    port?: number;
  };
  /** SSH public keys to put in the Container's authorized_keys file. */
  authorizedKeys?: Array<{
    name: string;
    publicKey: string;
  }>;
  /** Scheduling constraints for Container placement. */
  constraints?: {
    /** Limit Container placement to specific geographic regions. */
    regions?: Array<"ENAM" | "WNAM" | "EEUR" | "WEUR" | "APAC" | "SAM" | "ME" | "OC" | "AFR">;
    /** Restrict Containers to compliance boundaries. */
    jurisdiction?: "eu" | "fedramp";
  };
  rollout?: {
    /**
     * How a rollout should be created. It supports the following modes:
     *
     * - full-auto: The Container application will be rolled out fully
     *   automatically.
     * - none: The Container application will not have a rollout or update.
     * - full-manual: The Container application will be rolled out by manually
     *   progressing through the rollout steps.
     *
     * @default "full-auto"
     * @hidden
     */
    kind?: "full-auto" | "none" | "full-manual";
    /**
     * Configures what percentage of instances should be updated at each step of
     * a rollout. You can specify this as a single number or an array of numbers.
     *
     * If this is a single number, each step will progress by that percentage.
     * The options are 5, 10, 20, 25, 50, or 100.
     *
     * If this is an array, each step specifies the cumulative rollout progress.
     * The final step must be 100.
     *
     * @default [10, 100]
     */
    stepPercentage?: number | number[];
    /**
     * Configures the grace period, in seconds, for active instances before they
     * are shut down during a rollout.
     *
     * @default 0
     */
    activeGracePeriod?: number;
  };
}
/** A Container application managed by a Durable Object. */
interface DurableObjectContainerConfig extends BaseContainerConfig {
  schedulingPolicy: "durable-object";
  /** Named images that the Durable Object can start. */
  images?: Record<string, ContainerImage>;
}
/**
 * Container application configuration. This is the input shape passed to
 * `defineContainer` and is validated at runtime by `InputContainerSchema`.
 */
type ContainerConfig = DurableObjectContainerConfig | StandardContainerConfig;
/**
 * Worker configuration. This is the input shape passed to
 * [`defineWorker`](https://developers.cloudflare.com/workers/wrangler/configuration/).
 *
 * Fields are validated at runtime by `InputWorkerSchema` and normalised before
 * being passed to downstream tooling.
 */
interface WorkerConfig {
  /**
   * Discriminates this config as a Worker config.
   *
   * Injected automatically by `defineWorker`; only needs to be written by
   * hand when authoring a raw config object without the helper.
   */
  type: "worker";
  /**
   * The name of your Worker.
   */
  name: string;
  /**
   * A date in the form yyyy-mm-dd, which will be used to determine
   * which version of the Workers runtime is used.
   *
   * More details at https://developers.cloudflare.com/workers/configuration/compatibility-dates
   */
  compatibilityDate: string;
  /**
   * A list of flags that enable features from upcoming features of
   * the Workers runtime, usually used together with `compatibilityDate`.
   *
   * More details at https://developers.cloudflare.com/workers/configuration/compatibility-flags/
   *
   * @default []
   */
  compatibilityFlags?: string[];
  /**
   * The entrypoint module that will be executed.
   *
   * May be either a path string (e.g. `"./src/index.ts"`) or a module
   * namespace imported with the `cf-worker` import attribute.
   *
   * @example
   * ```ts
   * import * as entrypoint from "./src" with { type: "cf-worker" };
   * export default defineWorker({ entrypoint });
   * ```
   */
  entrypoint?: string | WorkerModule;
  /**
   * Specify the directory of static assets to deploy/serve.
   *
   * More details at https://developers.cloudflare.com/workers/frameworks/
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#assets
   */
  assets?: {
    /** How to handle HTML requests. */
    htmlHandling?: "auto-trailing-slash" | "drop-trailing-slash" | "force-trailing-slash" | "none";
    /** How to handle requests that do not match an asset. */
    notFoundHandling?: "single-page-application" | "404-page" | "none";
    /**
     * Matches will be routed to the User Worker, and matches to negative rules will go to the Asset Worker.
     *
     * Can also be `true`, indicating that every request should be routed to the User Worker.
     */
    runWorkerFirst?: string[] | boolean;
  };
  /**
   * Custom domains that your Worker should be published to.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#types-of-routes
   */
  domains?: string[];
  /**
   * Event triggers — fetch routes, queue consumers, cron schedules, Email
   * Routing addresses, and raw sockets — that invoke this Worker.
   * Construct entries with `triggers.fetch(...)`, `triggers.queue(...)`,
   * `triggers.scheduled(...)`, `triggers.email(...)`, or
   * `triggers.connect(...)`.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#triggers
   */
  triggers?: Trigger[];
  /**
   * A list of Tail Workers that are bound to this Worker.
   *
   * `@cloudflare/config` unifies regular and streaming tail consumers under
   * a single field; pass `streaming: true` to forward streaming tail events.
   *
   * @default []
   */
  tailConsumers?: Array<{
    /** The name of the service tail events will be forwarded to. */
    worker: string;
    /** Whether to stream tail events in real time. */
    streaming?: boolean;
  }>;
  /**
   * Specify the cache behavior of the Worker.
   */
  cache?: {
    /** If cache is enabled for this Worker. */
    enabled: boolean;
    /** Whether cached assets may be reused across Worker versions. */
    crossVersionCache?: boolean;
  };
  /**
   * Specify how the Worker should be located to minimize round-trip time.
   *
   * More details: https://developers.cloudflare.com/workers/platform/smart-placement/
   */
  placement?: {
    mode: "off" | "smart";
    hint?: string;
  } | {
    mode?: "targeted";
    region: string;
  } | {
    mode?: "targeted";
    host: string;
  } | {
    mode?: "targeted";
    hostname: string;
  };
  /**
   * Specify limits for runtime behavior.
   * Only supported for the "standard" Usage Model.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#limits
   */
  limits?: {
    /** Maximum allowed CPU time for a Worker's invocation in milliseconds. */
    cpuMs?: number;
    /** Maximum allowed number of fetch requests that a Worker's invocation can execute. */
    subrequests?: number;
  };
  /**
   * Send Trace Events from this Worker to Workers Logpush.
   *
   * This will not configure a corresponding Logpush job automatically.
   *
   * For more information about Workers Logpush, see:
   * https://blog.cloudflare.com/logpush-for-workers/
   */
  logpush?: boolean;
  /**
   * Specify the observability behavior of the Worker.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#observability
   */
  observability?: {
    /** If observability is enabled for this Worker. */
    enabled?: boolean;
    /** The sampling rate. */
    headSamplingRate?: number;
    /**
     * Whether query strings are removed from request URLs in logs and traces.
     *
     * @default false
     */
    redactQueryString?: boolean;
    logs?: {
      enabled?: boolean;
      /** The sampling rate. */
      headSamplingRate?: number;
      /** Set to false to disable invocation logs. */
      invocationLogs?: boolean;
      /**
       * If logs should be persisted to the Cloudflare observability platform where they can be queried in the dashboard.
       *
       * @default true
       */
      persist?: boolean;
      /**
       * What destinations logs emitted from the Worker should be sent to.
       *
       * @default []
       */
      destinations?: string[];
    };
    traces?: {
      enabled?: boolean;
      /** The sampling rate. */
      headSamplingRate?: number;
      /**
       * If traces should be persisted to the Cloudflare observability platform where they can be queried in the dashboard.
       *
       * @default true
       */
      persist?: boolean;
      /**
       * What destinations traces emitted from the Worker should be sent to.
       *
       * @default []
       */
      destinations?: string[];
    };
  };
  /**
   * Whether we use `<name>.<subdomain>.workers.dev` to
   * test and deploy your Worker.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#workersdev
   *
   * @default true
   */
  workersDev?: boolean;
  /**
   * Whether we use `<version>-<name>.<subdomain>.workers.dev` to
   * serve Preview URLs for your Worker.
   *
   * @default false
   */
  previewUrls?: boolean;
  /**
   * Designates this Worker as an internal-only "first-party" Worker.
   *
   * @internal
   */
  firstPartyWorker?: boolean;
  /**
   * "Unsafe" tables for runtime features that aren't directly supported by
   * this configuration. Values are forwarded verbatim in the Worker's
   * upload metadata.
   *
   * @default {}
   */
  unsafe?: {
    /**
     * Arbitrary key/value pairs that will be included in the uploaded metadata.  Values specified
     * here will always be applied to metadata last, so can add new or override existing fields.
     */
    metadata?: Record<string, unknown>;
    /**
     * Used for internal capnp uploads for the Workers runtime.
     */
    capnp?: {
      basePath: string;
      sourceSchemas: string[];
      compiledSchema?: never;
    } | {
      basePath?: never;
      sourceSchemas?: never;
      compiledSchema: string;
    };
  };
  /**
   * Bindings exposed on the Worker's `env` object. Construct entries with
   * `bindings.kv(...)`, `bindings.r2(...)`, etc.
   */
  env?: Record<string, Binding>;
  /**
   * Configuration for named exports declared by the Worker. Each entry's
   * key is the exported class name; the value configures the export.
   *
   * Only one export kind is currently supported:
   *
   * - Construct entries with `exports.durableObject(...)`.
   * - Declares Durable Object classes exported from this Worker.
   *   For more information about Durable Objects, see the documentation at
   *   https://developers.cloudflare.com/workers/learning/using-durable-objects.
   *   For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#durable-objects.
   */
  exports?: Record<string, Export>;
}
/**
 * Settings shared by the other exports.
 * Authored as a named `settings` export via
 * `defineSettings`.
 */
interface SettingsConfig {
  /**
   * Discriminates this config as a settings config.
   *
   * Injected automatically by `defineSettings`; only needs to be written by
   * hand when authoring a raw config object without the helper.
   */
  type: "settings";
  /**
   * This is the ID of the account associated with your zone.
   * You might have more than one account, so make sure to use
   * the ID of the account associated with the zone/route you
   * provide, if you provide one. It can also be specified through
   * the CLOUDFLARE_ACCOUNT_ID environment variable.
   */
  accountId?: string;
  /**
   * Specify the compliance region mode of the Worker.
   *
   * Although if the user does not specify a compliance region, the default is `public`,
   * it can be set to `undefined` in configuration to delegate to the CLOUDFLARE_COMPLIANCE_REGION environment variable.
   */
  complianceRegion?: "public" | "fedramp-high";
}
//#endregion
//#region src/worker-definition.d.ts
type WorkerConfigExport<T$1 extends WorkerConfig = WorkerConfig> = ConfigInput<T$1>;
/**
 * Authored Worker config shape — {@link WorkerConfig} without the `type`
 * discriminant, which `defineWorker` injects.
 */
type WorkerConfigInput = Omit<WorkerConfig, "type">;
declare const defineWorker: <const TInput extends ConfigInput<WorkerConfigInput>>(config: TInput) => TInput extends ((ctx: ConfigContext) => infer TResult) ? (ctx: ConfigContext) => TResult extends Promise<infer TConfig extends {
  [x: string]: unknown;
}> ? Promise<ConfigWithType<TConfig, "worker">> : TResult extends {
  [x: string]: unknown;
} ? ConfigWithType<TResult, "worker"> : never : TInput extends Promise<infer TConfig extends {
  [x: string]: unknown;
}> ? Promise<ConfigWithType<TConfig, "worker">> : TInput extends {
  [x: string]: unknown;
} ? ConfigWithType<TInput, "worker"> : never;
//#endregion
//#region src/bindings.d.ts
/** Options that control a binding during local development. */
interface BindingDevOptions {
  /** Whether the binding should connect to the remote resource. */
  remote?: boolean;
}
interface AgentMemoryBindingOptions {
  /** The user-chosen namespace name. Must exist in Cloudflare at deploy time. */
  namespace: string;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * Agent Memory namespace binding. Each binding is scoped to a namespace and
 * allows agents to persist and recall memory.
 */
interface AgentMemoryBinding extends AgentMemoryBindingOptions {
  type: "agent-memory";
}
interface AiBindingOptions {
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * Binding to the Workers AI project.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#workers-ai
 */
interface AiBinding extends AiBindingOptions {
  type: "ai";
}
/**
 * Binding to the Workers AI project.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#workers-ai
 */
interface TypedAiBinding<TAiModelList extends AiModelListType = AiModels> extends AiBinding {
  /** @internal Carries type parameters for inference */
  __typeParams: [TAiModelList];
}
interface AiSearchBindingOptions {
  /** The user-chosen instance name. Must exist in Cloudflare at deploy time. */
  name: string;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * AI Search instance binding. Each binding is bound directly to a single
 * pre-existing instance within the "default" namespace.
 */
interface AiSearchBinding extends AiSearchBindingOptions {
  type: "ai-search";
}
interface AiSearchNamespaceBindingOptions {
  /** The user-chosen namespace name. Must exist in Cloudflare at deploy time. */
  namespace: string;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * AI Search namespace binding. Each binding is scoped to a namespace and
 * allows dynamic instance CRUD within it.
 */
interface AiSearchNamespaceBinding extends AiSearchNamespaceBindingOptions {
  type: "ai-search-namespace";
}
interface AnalyticsEngineDatasetBindingOptions {
  /** The name of this dataset to write to. */
  name?: string;
}
/**
 * Binding to an Analytics Engine dataset.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#analytics-engine-datasets
 */
interface AnalyticsEngineDatasetBinding extends AnalyticsEngineDatasetBindingOptions {
  type: "analytics-engine-dataset";
}
interface ArtifactsBindingOptions {
  /** The namespace to use. */
  namespace: string;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * Binding to an Artifacts instance. Artifacts provides git-compatible file
 * storage on Cloudflare Workers.
 */
interface ArtifactsBinding extends ArtifactsBindingOptions {
  type: "artifacts";
}
/**
 * Binding to the Worker's static assets.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#assets
 */
interface AssetsBinding {
  type: "assets";
}
interface BrowserBindingOptions {
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * Binding to a headless browser usable from the Worker.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#browser-rendering
 */
interface BrowserBinding extends BrowserBindingOptions {
  type: "browser";
}
interface D1BindingOptions {
  /** The UUID of this D1 database (not required). */
  id?: string;
  /** The name of this D1 database. */
  name?: string;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * Binding to a D1 database.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#d1-databases
 */
interface D1Binding extends D1BindingOptions {
  type: "d1";
}
interface DispatchNamespaceBindingOptions {
  /** The namespace to bind to. */
  namespace?: string;
  /** Details about the outbound Worker which will handle outbound requests from your namespace. */
  outbound?: {
    /** Name of the Worker handling the outbound requests. */
    worker: string;
    /** (Optional) List of parameter names, for sending context from your dispatch Worker to the outbound handler. */
    parameters?: string[];
  };
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * Binding to a Workers for Platforms dispatch namespace.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#dispatch-namespace-bindings-workers-for-platforms
 */
interface DispatchNamespaceBinding extends DispatchNamespaceBindingOptions {
  type: "dispatch-namespace";
}
type WorkerReference = string | WorkerConfigExport;
type ReferencedWorkerConfig<TWorker$1 extends WorkerReference> = TWorker$1 extends string ? never : UnwrapConfig<TWorker$1>;
type DurableObjectExportName<TWorker$1 extends WorkerReference> = TWorker$1 extends string ? string : InferDurableNamespaces<ReferencedWorkerConfig<TWorker$1>>;
type WorkerEntrypointExportName<TWorker$1 extends WorkerReference> = TWorker$1 extends string ? string : InferWorkerEntrypointExports<ReferencedWorkerConfig<TWorker$1>>;
type WorkflowExportName<TWorker$1 extends WorkerReference> = TWorker$1 extends string ? string : InferExportsByType<ReferencedWorkerConfig<TWorker$1>, "workflow">;
interface DurableObjectBindingOptions<TWorker$1 extends WorkerReference = WorkerReference, TExportName$1 extends DurableObjectExportName<TWorker$1> = DurableObjectExportName<TWorker$1>> {
  /** The name or config of the Worker that defines the Durable Object class. */
  worker: TWorker$1;
  /** The exported class name of the Durable Object. */
  exportName: TExportName$1;
}
/**
 * Binding to a Durable Object class. `worker` is the name or config of the
 * Worker that defines the class; `exportName` is the exported class name.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#durable-objects
 */
interface DurableObjectBinding<TWorker$1 extends WorkerReference = WorkerReference, TExportName$1 extends DurableObjectExportName<TWorker$1> = DurableObjectExportName<TWorker$1>> extends DurableObjectBindingOptions<TWorker$1, TExportName$1> {
  type: "durable-object";
}
interface FlagshipBindingOptions {
  /** The Flagship app ID to bind to. */
  id?: string;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/** Binding to a Flagship feature-flag service. */
interface FlagshipBinding extends FlagshipBindingOptions {
  type: "flagship";
}
interface HyperdriveBindingOptions {
  /** The ID of the Hyperdrive configuration. */
  id: string;
  /** Options that only apply during local development. */
  dev?: {
    /** The database connection string used during local development. */
    connectionString?: string;
  };
}
/**
 * Binding to a Hyperdrive configuration.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#hyperdrive
 */
interface HyperdriveBinding extends HyperdriveBindingOptions {
  type: "hyperdrive";
}
interface ImagesBindingOptions {
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * Binding to Cloudflare Images.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#images
 */
interface ImagesBinding$1 extends ImagesBindingOptions {
  type: "images";
}
/**
 * Inline JSON value made available to the Worker on `env` under the
 * binding name.
 */
interface JsonBinding<T$1 extends Json = Json> {
  type: "json";
  /** The JSON value made available to the Worker. */
  value: T$1;
}
interface KvBindingOptions {
  /** The ID of the KV namespace. */
  id?: string;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * Binding to a Workers KV namespace.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#kv-namespaces
 */
interface KvBinding extends KvBindingOptions {
  type: "kv";
}
/**
 * Binding to a Workers KV namespace.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#kv-namespaces
 */
interface TypedKvBinding<TKey extends string = string> extends KvBinding {
  /** @internal Carries type parameters for inference */
  __typeParams: [TKey];
}
interface LogfwdrBindingOptions {
  /** The destination for this logged message. */
  destination: string;
}
/** Binding for forwarding logs to logfwdr. */
interface LogfwdrBinding extends LogfwdrBindingOptions {
  type: "logfwdr";
}
interface MediaBindingOptions {
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/** Binding to Cloudflare Media Transformations. */
interface MediaBinding$1 extends MediaBindingOptions {
  type: "media";
}
interface MtlsCertificateBindingOptions {
  /** The UUID of the uploaded mTLS certificate. */
  id: string;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * Binding to an uploaded mTLS certificate.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#mtls-certificates
 */
interface MtlsCertificateBinding extends MtlsCertificateBindingOptions {
  type: "mtls-certificate";
}
interface PipelineBindingOptions {
  /** Name of the Pipeline to bind. */
  name: string;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/** Binding to a Cloudflare Pipeline. */
interface PipelineBinding extends PipelineBindingOptions {
  type: "pipeline";
}
/** Binding to a Cloudflare Pipeline. */
interface TypedPipelineBinding<TRecord extends PipelineRecord = PipelineRecord> extends PipelineBinding {
  /** @internal Carries type parameters for inference */
  __typeParams: [TRecord];
}
interface QueueBindingOptions {
  /** The name of this Queue. */
  name?: string;
  /** The number of seconds to wait before delivering a message. */
  deliveryDelay?: number;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * Producer binding to a Cloudflare Queue.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#queues
 */
interface QueueBinding extends QueueBindingOptions {
  type: "queue";
}
/**
 * Producer binding to a Cloudflare Queue.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#queues
 */
interface TypedQueueBinding<TBody = unknown> extends QueueBinding {
  /** @internal Carries type parameters for inference */
  __typeParams: [TBody];
}
interface R2BindingOptions {
  /** The name of this R2 bucket at the edge. */
  name?: string;
  /** The jurisdiction that the bucket exists in. Default if not present. */
  jurisdiction?: string;
  /** Settings that only apply to local development. */
  dev?: BindingDevOptions & {
    /** EXPERIMENTAL: credentials for the local S3-compatible endpoint. */
    experimentalS3Credentials?: {
      accessKeyId: string;
      secretAccessKey: string;
    };
  };
}
/**
 * Binding to an R2 bucket.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#r2-buckets
 */
interface R2Binding extends R2BindingOptions {
  type: "r2";
}
interface RateLimitBindingOptions {
  /** The namespace ID for this rate limiter. */
  namespace: string;
  /** Simple rate limiting configuration. */
  simple: {
    /** The maximum number of requests allowed in the time period. */
    limit: number;
    /** The time period in seconds (10 for ten seconds, 60 for one minute). */
    period: 10 | 60;
  };
}
/** Binding to a rate limiter. */
interface RateLimitBinding extends RateLimitBindingOptions {
  type: "rate-limit";
}
/**
 * Declares a secret that is required by your Worker, exposed on `env` under
 * the binding name.
 *
 * When defined, this binding:
 * - Replaces .dev.vars/.env/process.env inference for type generation
 * - Enables local dev validation with warnings for missing secrets
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#secrets-configuration-property
 */
interface SecretBinding {
  type: "secret";
}
interface SecretsStoreSecretBindingOptions {
  /** ID of the secret store. */
  storeId: string;
  /** Name of the secret. */
  secretName: string;
}
/** Binding to a Secrets Store secret. */
interface SecretsStoreSecretBinding extends SecretsStoreSecretBindingOptions {
  type: "secrets-store-secret";
}
type SendEmailDestinationOptions = {
  /** If this binding should be restricted to a specific verified address. */
  destinationAddress: string;
  allowedDestinationAddresses?: never;
} | {
  destinationAddress?: never;
  /** If this binding should be restricted to a set of verified addresses. */
  allowedDestinationAddresses: string[];
} | {
  destinationAddress?: never;
  allowedDestinationAddresses?: never;
};
type SendEmailBindingOptions = SendEmailDestinationOptions & {
  /** If this binding should be restricted to a set of sender addresses. */
  allowedSenderAddresses?: string[];
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
};
/**
 * Binding for sending email from inside the Worker.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#email-bindings
 */
type SendEmailBinding = SendEmailBindingOptions & {
  type: "send-email";
};
interface StreamBindingOptions {
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/** Binding to Cloudflare Stream. */
interface StreamBinding$1 extends StreamBindingOptions {
  type: "stream";
}
/**
 * Inline string value made available to the Worker on `env` under the
 * binding name.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#environment-variables
 */
interface TextBinding<T$1 extends string = string> {
  type: "text";
  /** The string value made available to the Worker. */
  value: T$1;
}
interface UnsafeBindingOptions {
  /** Local-dev plugin configuration for this unsafe binding. */
  dev?: {
    /** The plugin package that provides the binding's local-dev implementation. */
    plugin: {
      package: string;
      name: string;
    };
    /** Plugin-specific options. */
    options?: Record<string, unknown>;
  };
  [key: string]: unknown;
}
/**
 * Escape-hatch binding for runtime features that aren't directly supported
 * by this configuration. Included in the Worker's upload metadata without
 * changes.
 */
interface UnsafeBinding extends UnsafeBindingOptions {
  type: `unsafe:${string}`;
}
interface VectorizeBindingOptions {
  /** The name of the Vectorize index. */
  name: string;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * Binding to a Vectorize index.
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#vectorize-indexes
 */
interface VectorizeBinding extends VectorizeBindingOptions {
  type: "vectorize";
}
/** Binding to the Worker version's metadata. */
interface VersionMetadataBinding {
  type: "version-metadata";
}
type VpcNetworkBindingOptions = {
  /** The tunnel ID of the Cloudflare Tunnel to route traffic through. Mutually exclusive with `networkId`. */
  tunnelId: string;
  networkId?: never;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
} | {
  tunnelId?: never;
  /** The network ID to route traffic through. Mutually exclusive with `tunnelId`. */
  networkId: string;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
};
/** Binding to a VPC network. */
type VpcNetworkBinding = VpcNetworkBindingOptions & {
  type: "vpc-network";
};
interface VpcServiceBindingOptions {
  /** The service ID of the VPC connectivity service. */
  id: string;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/** Binding to a VPC service. */
interface VpcServiceBinding extends VpcServiceBindingOptions {
  type: "vpc-service";
}
interface WorkerBindingOptions<TWorker$1 extends WorkerReference = WorkerReference, TExportName$1 extends WorkerEntrypointExportName<TWorker$1> | undefined = WorkerEntrypointExportName<TWorker$1> | undefined> {
  /** The name or config of the bound Worker. */
  worker: TWorker$1;
  /** The named export to bind to (defaults to the default export). */
  exportName?: TExportName$1;
  /** Optional properties that will be made available to the service via `ctx.props`. */
  props?: Record<string, unknown>;
  /** Options that only apply during local development. */
  dev?: BindingDevOptions;
}
/**
 * Service binding (Worker-to-Worker). `worker` is the name or config of the
 * bound Worker; `exportName` selects a named `WorkerEntrypoint` export
 * (defaults to the default export).
 *
 * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#service-bindings
 */
interface WorkerBinding<TWorker$1 extends WorkerReference = WorkerReference, TExportName$1 extends WorkerEntrypointExportName<TWorker$1> | undefined = WorkerEntrypointExportName<TWorker$1> | undefined> extends WorkerBindingOptions<TWorker$1, TExportName$1> {
  type: "worker";
}
/** Binding to a Worker Loader. */
interface WorkerLoaderBinding {
  type: "worker-loader";
}
interface WorkflowBindingOptions<TWorker$1 extends WorkerReference = WorkerReference, TExportName$1 extends WorkflowExportName<TWorker$1> = WorkflowExportName<TWorker$1>> {
  /** The name or config of the Worker that defines the Workflow. */
  worker: TWorker$1;
  /** The exported class name of the Workflow. */
  exportName: TExportName$1;
}
/**
 * Binding to a Workflow. `worker` is the name or config of the Worker that
 * defines the Workflow; `exportName` is the exported `WorkflowEntrypoint`
 * class name.
 */
interface WorkflowBinding<TWorker$1 extends WorkerReference = WorkerReference, TExportName$1 extends WorkflowExportName<TWorker$1> = WorkflowExportName<TWorker$1>> extends WorkflowBindingOptions<TWorker$1, TExportName$1> {
  type: "workflow";
}
interface Bindings {
  /**
   * Agent Memory namespace binding. Each binding is scoped to a namespace and
   * allows agents to persist and recall memory.
   */
  agentMemory(options: AgentMemoryBindingOptions): AgentMemoryBinding;
  /**
   * Binding to the Workers AI project.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#workers-ai
   */
  ai<TAiModelList extends AiModelListType = AiModels>(options?: AiBindingOptions): TypedAiBinding<TAiModelList>;
  /**
   * AI Search instance binding. Each binding is bound directly to a single
   * pre-existing instance within the "default" namespace.
   */
  aiSearch(options: AiSearchBindingOptions): AiSearchBinding;
  /**
   * AI Search namespace binding. Each binding is scoped to a namespace and
   * allows dynamic instance CRUD within it.
   */
  aiSearchNamespace(options: AiSearchNamespaceBindingOptions): AiSearchNamespaceBinding;
  /**
   * Binding to an Analytics Engine dataset.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#analytics-engine-datasets
   */
  analyticsEngineDataset(options?: AnalyticsEngineDatasetBindingOptions): AnalyticsEngineDatasetBinding;
  /**
   * Binding to an Artifacts instance. Artifacts provides git-compatible file
   * storage on Cloudflare Workers.
   */
  artifacts(options: ArtifactsBindingOptions): ArtifactsBinding;
  /**
   * Binding to the Worker's static assets.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#assets
   */
  assets(): AssetsBinding;
  /**
   * Binding to a headless browser usable from the Worker.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#browser-rendering
   */
  browser(options?: BrowserBindingOptions): BrowserBinding;
  /**
   * Binding to a D1 database.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#d1-databases
   */
  d1(options?: D1BindingOptions): D1Binding;
  /**
   * Binding to a Workers for Platforms dispatch namespace.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#dispatch-namespace-bindings-workers-for-platforms
   */
  dispatchNamespace(options?: DispatchNamespaceBindingOptions): DispatchNamespaceBinding;
  /**
   * Binding to a Durable Object class. `worker` is the name or config of the
   * Worker that defines the class; `exportName` is the exported class name.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#durable-objects
   */
  durableObject<TWorker$1 extends WorkerReference, TExportName$1 extends DurableObjectExportName<TWorker$1>>(options: DurableObjectBindingOptions<TWorker$1, TExportName$1>): DurableObjectBinding<TWorker$1, TExportName$1>;
  /** Binding to a Flagship feature-flag service. */
  flagship(options?: FlagshipBindingOptions): FlagshipBinding;
  /**
   * Binding to a Hyperdrive configuration.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#hyperdrive
   */
  hyperdrive(options: HyperdriveBindingOptions): HyperdriveBinding;
  /**
   * Binding to Cloudflare Images.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#images
   */
  images(options?: ImagesBindingOptions): ImagesBinding$1;
  /**
   * Inline JSON value made available to the Worker on `env` under the
   * binding name.
   */
  json<T$1 extends Json>(value: T$1): JsonBinding<T$1>;
  /**
   * Binding to a Workers KV namespace.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#kv-namespaces
   */
  kv<TKey extends string = string>(options?: KvBindingOptions): TypedKvBinding<TKey>;
  /** Binding for forwarding logs to logfwdr. */
  logfwdr(options: LogfwdrBindingOptions): LogfwdrBinding;
  /** Binding to Cloudflare Media Transformations. */
  media(options?: MediaBindingOptions): MediaBinding$1;
  /**
   * Binding to an uploaded mTLS certificate.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#mtls-certificates
   */
  mtlsCertificate(options: MtlsCertificateBindingOptions): MtlsCertificateBinding;
  /** Binding to a Cloudflare Pipeline. */
  pipeline<TRecord extends PipelineRecord = PipelineRecord>(options: PipelineBindingOptions): TypedPipelineBinding<TRecord>;
  /**
   * Producer binding to a Cloudflare Queue.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#queues
   */
  queue<TBody = unknown>(options?: QueueBindingOptions): TypedQueueBinding<TBody>;
  /**
   * Binding to an R2 bucket.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#r2-buckets
   */
  r2(options?: R2BindingOptions): R2Binding;
  /** Binding to a rate limiter. */
  rateLimit(options: RateLimitBindingOptions): RateLimitBinding;
  /**
   * Declares a secret that is required by your Worker, exposed on `env` under
   * the binding name.
   *
   * When defined, this binding:
   * - Replaces .dev.vars/.env/process.env inference for type generation
   * - Enables local dev validation with warnings for missing secrets
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#secrets-configuration-property
   */
  secret(): SecretBinding;
  /** Binding to a Secrets Store secret. */
  secretsStoreSecret(options: SecretsStoreSecretBindingOptions): SecretsStoreSecretBinding;
  /**
   * Binding for sending email from inside the Worker.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#email-bindings
   */
  sendEmail(options?: SendEmailBindingOptions): SendEmailBinding;
  /** Binding to Cloudflare Stream. */
  stream(options?: StreamBindingOptions): StreamBinding$1;
  /**
   * Inline string value made available to the Worker on `env` under the
   * binding name.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#environment-variables
   */
  text<T$1 extends string>(value: T$1): TextBinding<T$1>;
  /**
   * Binding to a Vectorize index.
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#vectorize-indexes
   */
  vectorize(options: VectorizeBindingOptions): VectorizeBinding;
  /** Binding to the Worker version's metadata. */
  versionMetadata(): VersionMetadataBinding;
  /** Binding to a VPC network. */
  vpcNetwork(options: VpcNetworkBindingOptions): VpcNetworkBinding;
  /** Binding to a VPC service. */
  vpcService(options: VpcServiceBindingOptions): VpcServiceBinding;
  /**
   * Service binding (Worker-to-Worker). `worker` is the name or config of the
   * bound Worker; `exportName` selects a named `WorkerEntrypoint` export
   * (defaults to the default export).
   *
   * For reference, see https://developers.cloudflare.com/workers/wrangler/configuration/#service-bindings
   */
  worker<TWorker$1 extends WorkerReference, TExportName$1 extends WorkerEntrypointExportName<TWorker$1> | undefined = undefined>(options: WorkerBindingOptions<TWorker$1, TExportName$1>): WorkerBinding<TWorker$1, NoInfer<TExportName$1>>;
  /** Binding to a Worker Loader. */
  workerLoader(): WorkerLoaderBinding;
}
declare const bindings: Bindings;
//#endregion
//#region src/settings-definition.d.ts
type SettingsConfigExport<T$1 extends SettingsConfig = SettingsConfig> = ConfigInput<T$1>;
/**
 * Authored settings config shape — {@link SettingsConfig} without the `type`
 * discriminant, which `defineSettings` injects.
 */
type SettingsConfigInput = Omit<SettingsConfig, "type">;
/**
 * Declare shared settings.
 * Authored as a named `settings` export.
 */
declare const defineSettings: <const TInput extends ConfigInput<SettingsConfigInput>>(config: TInput) => TInput extends ((ctx: ConfigContext) => infer TResult) ? (ctx: ConfigContext) => TResult extends Promise<infer TConfig extends {
  [x: string]: unknown;
}> ? Promise<ConfigWithType<TConfig, "settings">> : TResult extends {
  [x: string]: unknown;
} ? ConfigWithType<TResult, "settings"> : never : TInput extends Promise<infer TConfig extends {
  [x: string]: unknown;
}> ? Promise<ConfigWithType<TConfig, "settings">> : TInput extends {
  [x: string]: unknown;
} ? ConfigWithType<TInput, "settings"> : never;
//#endregion
//#endregion
//#region src/experimental-config/types.d.ts
/**
 * The shape of `wrangler.config.ts` — tooling / bundling / dev-server
 * configuration that complements the Worker configuration authored in
 * `cloudflare.config.ts` via `defineWorker`.
 */
interface WranglerConfig {
  noBundle?: boolean;
  minify?: boolean;
  keepNames?: boolean;
  alias?: Record<string, string>;
  define?: Record<string, string>;
  findAdditionalModules?: boolean;
  preserveFileNames?: boolean;
  baseDir?: string;
  rules?: Array<{
    type: "ESModule" | "CommonJS" | "CompiledWasm" | "Text" | "Data" | "PythonModule" | "PythonRequirement";
    globs: string[];
    fallthrough?: boolean;
  }>;
  wasmModules?: Record<string, string>;
  textBlobs?: Record<string, string>;
  dataBlobs?: Record<string, string>;
  tsconfig?: string;
  jsxFactory?: string;
  jsxFragment?: string;
  pythonModules?: {
    exclude?: string[];
  };
  uploadSourceMaps?: boolean;
  build?: {
    command?: string;
    cwd?: string;
    watchDir?: string | string[];
  };
  /**
   * Assets directory — the only tooling-side asset setting. The runtime
   * asset fields (`binding`, `htmlHandling`, `notFoundHandling`,
   * `runWorkerFirst`) live in `cloudflare.config.ts` under `assets`.
   */
  assetsDirectory?: string;
  dev?: {
    ip?: string;
    port?: number;
    inspectorPort?: number;
    inspectorIp?: string;
    localProtocol?: "http" | "https";
    upstreamProtocol?: "http" | "https";
    host?: string;
    /**
     * Type-generation settings. Consumed directly by the new-config
     * type-generation path (`regenerateNewConfigTypes`) — NOT threaded
     * through the merged `RawConfig`. Default:
     * `{ generate: true, includeRuntime: true }`.
     *
     * - `generate`: emit `worker-configuration.d.ts`.
     * - `includeRuntime`: append the Workers runtime types (generated from
     *   the project's compatibility date/flags).
     */
    types?: {
      generate?: boolean;
      includeRuntime?: boolean;
    };
    /**
     * Container-related dev settings. `containers` itself is currently not
     * supported under `--experimental-new-config`, but these dev-time settings are
     * accepted so users can enable them ahead of `containers` opening up.
     * They're no-ops when `containers` is absent.
     */
    enableContainers?: boolean;
    /**
     * Either the Docker unix socket (e.g. `unix:///var/run/docker.sock`) or
     * a full configuration. The string form is the common case; the full
     * object form (with `localDocker.socketPath` and TLS settings) can be
     * added later if needed.
     */
    containerEngine?: string;
  };
  sendMetrics?: boolean;
}
//#endregion
//#region src/experimental-config/wrangler-definition.d.ts
type WranglerConfigExport = WranglerConfig | Promise<WranglerConfig> | ((ctx: ConfigContext) => WranglerConfig | Promise<WranglerConfig>);
declare function defineWranglerConfig(config: WranglerConfigExport): WranglerConfigExport;
//#endregion
export { AgentMemoryBinding, AiBinding, AiSearchBinding, AiSearchNamespaceBinding, AnalyticsEngineDatasetBinding, ArtifactsBinding, AssetsBinding, BindingDevOptions, Bindings, BrowserBinding, ConfigContext, ConnectTrigger, ContainerConfig, ContainerConfigExport, ContainerConfigInput, D1Binding, DispatchNamespaceBinding, DurableObjectBinding, DurableObjectCreatedExport, DurableObjectDeletedExport, DurableObjectExpectingTransferExport, DurableObjectRenamedExport, DurableObjectTransferredExport, EmailTrigger, Exports, FetchTrigger, FlagshipBinding, HyperdriveBinding, ImagesBinding$1 as ImagesBinding, InferDurableNamespaces, InferEnv, InferMainModule, JsonBinding, KvBinding, LogfwdrBinding, MediaBinding$1 as MediaBinding, MtlsCertificateBinding, PipelineBinding, QueueBinding, QueueConsumerTrigger, R2Binding, RateLimitBinding, ScheduledTrigger, SecretBinding, SecretsStoreSecretBinding, SendEmailBinding, SettingsConfig, SettingsConfigExport, SettingsConfigInput, StreamBinding$1 as StreamBinding, TextBinding, Triggers, TypedAiBinding, TypedKvBinding, TypedPipelineBinding, TypedQueueBinding, UnsafeBinding, UnwrapConfig, VectorizeBinding, VersionMetadataBinding, VpcNetworkBinding, VpcServiceBinding, WorkerBinding, WorkerConfig, WorkerConfigExport, WorkerConfigInput, WorkerEntrypointExport, WorkerEntrypointExportOptions, WorkerLoaderBinding, WorkerReference, WorkflowBinding, WranglerConfig, WranglerConfigExport, bindings, defineContainer, defineSettings, defineWorker, defineWranglerConfig, exports, triggers };
//# sourceMappingURL=experimental-config.d.mts.map