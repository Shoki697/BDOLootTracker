// src/workers/flagship/binding.worker.ts
import { WorkerEntrypoint } from "cloudflare:workers";

// src/workers/flagship/constants.ts
var ADMIN_API = "FlagshipBinding::admin_api";

// src/workers/flagship/evaluate.ts
var TypeCastError = class extends Error {
  constructor(flagKey, expectedType, actualValue) {
    super(
      `Flag '${flagKey}' has type '${typeof actualValue}', expected '${expectedType}'`
    ), this.name = "TypeCastError";
  }
}, FlagConfigError = class extends Error {
  constructor(flagKey, message) {
    super(`Flag '${flagKey}' ${message}`), this.name = "FlagConfigError";
  }
}, ISO_8601_REGEX = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$/, encoder = new TextEncoder(), randomBuf = new Uint32Array(1), hashBuf = new Uint8Array(512);
function murmurhash3(str, seed) {
  hashBuf.byteLength < str.length * 3 && (hashBuf = new Uint8Array(str.length * 3));
  let { written: n } = encoder.encodeInto(str, hashBuf), b = hashBuf, h = seed >>> 0, i = 0;
  for (; i + 4 <= n; ) {
    let k2 = b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24;
    k2 = Math.imul(k2, 3432918353) >>> 0, k2 = (k2 << 15 | k2 >>> 17) >>> 0, k2 = Math.imul(k2, 461845907) >>> 0, h ^= k2, h = (h << 13 | h >>> 19) >>> 0, h = Math.imul(h, 5) + 3864292196 >>> 0, i += 4;
  }
  let k = 0;
  return n - i >= 3 && (k ^= b[i + 2] << 16), n - i >= 2 && (k ^= b[i + 1] << 8), n > i && (k ^= b[i], k = Math.imul(k, 3432918353) >>> 0, k = (k << 15 | k >>> 17) >>> 0, k = Math.imul(k, 461845907) >>> 0, h ^= k), h ^= n, h ^= h >>> 16, h = Math.imul(h, 2246822507) >>> 0, h ^= h >>> 13, h = Math.imul(h, 3266489909) >>> 0, h ^= h >>> 16, (h >>> 0) % 100;
}
function compareTemporalOrNumeric(attrValue, target, compare) {
  if (typeof target == "string" && ISO_8601_REGEX.test(target) && typeof attrValue == "string") {
    let ts = Date.parse(attrValue);
    if (!isNaN(ts))
      return compare(ts, Date.parse(target));
  }
  return compare(Number(attrValue), Number(target));
}
function evaluateCondition(condition, context) {
  if ("logical_operator" in condition) {
    let { logical_operator, clauses } = condition;
    if (logical_operator === "AND") {
      for (let clause of clauses)
        if (!evaluateCondition(clause, context))
          return !1;
      return !0;
    }
    for (let clause of clauses)
      if (evaluateCondition(clause, context))
        return !0;
    return !1;
  }
  let { attribute, operator, value: target } = condition, attrValue = context[attribute];
  if (attrValue === void 0)
    return !1;
  switch (operator) {
    case "equals":
      return String(attrValue) === String(target);
    case "not_equals":
      return String(attrValue) !== String(target);
    case "contains":
      return String(attrValue).includes(String(target));
    case "starts_with":
      return String(attrValue).startsWith(String(target));
    case "ends_with":
      return String(attrValue).endsWith(String(target));
    case "greater_than":
      return compareTemporalOrNumeric(attrValue, target, (a, b) => a > b);
    case "less_than":
      return compareTemporalOrNumeric(attrValue, target, (a, b) => a < b);
    case "greater_than_or_equals":
      return compareTemporalOrNumeric(attrValue, target, (a, b) => a >= b);
    case "less_than_or_equals":
      return compareTemporalOrNumeric(attrValue, target, (a, b) => a <= b);
    case "in":
      return Array.isArray(target) && target.some((value) => String(value) === String(attrValue));
    case "not_in":
      return Array.isArray(target) && !target.some((value) => String(value) === String(attrValue));
    default:
      return !1;
  }
}
function evaluateFlag(flagDef, context, accountId) {
  let serve = (variant, reason) => {
    if (!Object.hasOwn(flagDef.variations, variant))
      throw new FlagConfigError(
        flagDef.key,
        `variation '${variant}' is not defined`
      );
    return {
      value: flagDef.variations[variant],
      variant,
      reason
    };
  };
  if (!flagDef.enabled)
    return serve(flagDef.default_variation, "DISABLED");
  let seed, rules = [...flagDef.rules].sort((a, b) => {
    let aPriority = "priority" in a ? a.priority : 0, bPriority = "priority" in b ? b.priority : 0;
    return aPriority - bPriority;
  });
  for (let rule of rules) {
    let ruleMatches = !0;
    for (let condition of rule.conditions)
      if (!evaluateCondition(condition, context)) {
        ruleMatches = !1;
        break;
      }
    if (ruleMatches && rule.rollout !== void 0 && rule.rollout.percentage < 100) {
      seed ??= murmurhash3(`${accountId}:${flagDef.key}`, 0);
      let attr = context[rule.rollout.attribute || "targetingKey"];
      (attr != null ? murmurhash3(String(attr), seed) : crypto.getRandomValues(randomBuf)[0] / 4294967296 * 100) >= rule.rollout.percentage && (ruleMatches = !1);
    }
    if (ruleMatches)
      return serve(
        rule.serve_variation,
        rule.rollout !== void 0 ? "SPLIT" : "TARGETING_MATCH"
      );
  }
  return serve(flagDef.default_variation, "DEFAULT");
}
function matchesType(value, expectedType) {
  switch (expectedType) {
    case "boolean":
      return typeof value == "boolean";
    case "string":
      return typeof value == "string";
    case "number":
      return typeof value == "number";
    case "object":
      return typeof value == "object" && value !== null;
    default:
      return !1;
  }
}

// src/workers/flagship/flags.ts
function flagNotFoundMessage(flagKey) {
  return `Flag '${flagKey}' not found`;
}

// src/workers/flagship/binding.worker.ts
var FlagNotFoundError = class extends Error {
  constructor(flagKey) {
    super(flagNotFoundMessage(flagKey));
  }
}, FlagConflictError = class extends Error {
  constructor(flagKey) {
    super(`Flag '${flagKey}' already exists`);
  }
}, warnedAboutUnseededRollout = !1;
function validateAccountTag(accountTag) {
  if (typeof accountTag != "string" || accountTag === "")
    throw new Error("accountTag must be a non-empty string");
}
function hasPartialRollout(flag) {
  return flag.rules.some(
    (rule) => rule.rollout !== void 0 && rule.rollout.percentage < 100
  );
}
function warnIfBucketingUnseeded(flag) {
  warnedAboutUnseededRollout || !hasPartialRollout(flag) || (warnedAboutUnseededRollout = !0, console.warn(
    `Flagship: flag '${flag.key}' has a percentage rollout, but the local flag store has no account tag, so its buckets will not match your remote app. Run \`flagship flags pull\` to seed the store.`
  ));
}
function errorCodeFor(error) {
  if (error instanceof FlagNotFoundError)
    return "FLAG_NOT_FOUND";
  if (error instanceof FlagConfigError)
    return "PARSE_ERROR";
}
var FlagshipBinding = class extends WorkerEntrypoint {
  get #stub() {
    let namespace = this.env.store;
    return namespace.get(namespace.idFromName(this.ctx.props.appId));
  }
  async #evaluate(flagKey, context) {
    if (typeof flagKey != "string" || flagKey === "")
      throw new Error("flagKey must be a non-empty string");
    let { flag, accountTag } = await this.#stub.getForEvaluation(flagKey);
    if (flag === null)
      throw new FlagNotFoundError(flagKey);
    accountTag === null && warnIfBucketingUnseeded(flag);
    let { value, variant, reason } = evaluateFlag(
      flag,
      context,
      accountTag ?? this.ctx.props.accountTag
    );
    return { flagKey, value, variant, reason };
  }
  async #typedDetails(flagKey, defaultValue, expectedType, context) {
    let failure = (errorCode, errorMessage) => ({
      flagKey,
      value: defaultValue,
      variant: "default",
      reason: "ERROR",
      errorCode,
      errorMessage
    }), result;
    try {
      result = await this.#evaluate(flagKey, context ?? {});
    } catch (error) {
      let errorCode = errorCodeFor(error);
      if (errorCode === void 0)
        throw error;
      return failure(errorCode, error.message);
    }
    return matchesType(result.value, expectedType) ? {
      flagKey,
      value: result.value,
      variant: result.variant,
      reason: result.reason
    } : failure(
      "TYPE_MISMATCH",
      new TypeCastError(flagKey, expectedType, result.value).message
    );
  }
  async get(flagKey, defaultValue, context) {
    try {
      return (await this.#evaluate(flagKey, context ?? {})).value;
    } catch (error) {
      if (errorCodeFor(error) !== void 0 && defaultValue !== void 0)
        return defaultValue;
      throw error;
    }
  }
  async getBooleanValue(flagKey, defaultValue, context) {
    return (await this.#typedDetails(flagKey, defaultValue, "boolean", context)).value;
  }
  async getStringValue(flagKey, defaultValue, context) {
    return (await this.#typedDetails(flagKey, defaultValue, "string", context)).value;
  }
  async getNumberValue(flagKey, defaultValue, context) {
    return (await this.#typedDetails(flagKey, defaultValue, "number", context)).value;
  }
  async getObjectValue(flagKey, defaultValue, context) {
    return (await this.#typedDetails(flagKey, defaultValue, "object", context)).value;
  }
  async getBooleanDetails(flagKey, defaultValue, context) {
    return this.#typedDetails(flagKey, defaultValue, "boolean", context);
  }
  async getStringDetails(flagKey, defaultValue, context) {
    return this.#typedDetails(flagKey, defaultValue, "string", context);
  }
  async getNumberDetails(flagKey, defaultValue, context) {
    return this.#typedDetails(flagKey, defaultValue, "number", context);
  }
  async getObjectDetails(flagKey, defaultValue, context) {
    return this.#typedDetails(flagKey, defaultValue, "object", context);
  }
  [ADMIN_API]() {
    let stub = this.#stub;
    function unwrap(result, flagKey) {
      switch (result.status) {
        case "written":
          return result.flag;
        case "missing":
          throw new FlagNotFoundError(flagKey);
        case "exists":
          throw new FlagConflictError(flagKey);
        case "invalid":
          throw new Error(result.message);
      }
    }
    async function getFlag(flagKey) {
      let flag = await stub.get(flagKey);
      if (flag === null)
        throw new FlagNotFoundError(flagKey);
      return flag;
    }
    async function write(operation, flagKey) {
      return unwrap(await operation, flagKey);
    }
    async function writeInput(operation, input) {
      let flagKey = typeof input == "object" && input !== null && "key" in input && typeof input.key == "string" ? input.key : "";
      return unwrap(await operation, flagKey);
    }
    return {
      listFlags: () => stub.list(),
      getFlag,
      getAccountTag: () => stub.getAccountTag(),
      setAccountTag: (accountTag) => (validateAccountTag(accountTag), stub.setAccountTag(accountTag)),
      createFlag: (input) => writeInput(stub.create(input), input),
      updateFlag: (flagKey, input) => write(stub.update(flagKey, input), flagKey),
      patchFlag: (flagKey, changes) => write(stub.patch(flagKey, changes), flagKey),
      putFlag: (input) => writeInput(stub.put(input), input),
      putFlags: async (inputs, accountTag) => {
        validateAccountTag(accountTag);
        let result = await stub.putAll(inputs, accountTag);
        if (result.status === "invalid")
          throw new Error(result.message);
      },
      deleteFlag: async (flagKey) => {
        if (!await stub.delete(flagKey))
          throw new FlagNotFoundError(flagKey);
      },
      evaluateFlag: (flagKey, context) => this.#evaluate(flagKey, context ?? {})
    };
  }
};
export {
  FlagshipBinding
};
//# sourceMappingURL=binding.worker.js.map
