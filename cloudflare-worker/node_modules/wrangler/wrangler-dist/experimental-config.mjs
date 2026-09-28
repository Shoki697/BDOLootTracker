//#region ../config/dist/public-Dyr8BJqf.mjs
const bindings = {
	agentMemory: (options) => ({
		type: "agent-memory",
		...options
	}),
	ai: (options) => ({
		type: "ai",
		...options
	}),
	aiSearch: (options) => ({
		type: "ai-search",
		...options
	}),
	aiSearchNamespace: (options) => ({
		type: "ai-search-namespace",
		...options
	}),
	analyticsEngineDataset: (options) => ({
		type: "analytics-engine-dataset",
		...options
	}),
	artifacts: (options) => ({
		type: "artifacts",
		...options
	}),
	assets: () => ({ type: "assets" }),
	browser: (options) => ({
		type: "browser",
		...options
	}),
	d1: (options) => ({
		type: "d1",
		...options
	}),
	dispatchNamespace: (options) => ({
		type: "dispatch-namespace",
		...options
	}),
	durableObject: (options) => ({
		type: "durable-object",
		...options
	}),
	flagship: (options) => ({
		type: "flagship",
		...options
	}),
	hyperdrive: (options) => ({
		type: "hyperdrive",
		...options
	}),
	images: (options) => ({
		type: "images",
		...options
	}),
	json: (value) => ({
		type: "json",
		value
	}),
	kv: (options) => ({
		type: "kv",
		...options
	}),
	logfwdr: (options) => ({
		type: "logfwdr",
		...options
	}),
	media: (options) => ({
		type: "media",
		...options
	}),
	mtlsCertificate: (options) => ({
		type: "mtls-certificate",
		...options
	}),
	pipeline: (options) => ({
		type: "pipeline",
		...options
	}),
	queue: (options) => ({
		type: "queue",
		...options
	}),
	rateLimit: (options) => ({
		type: "rate-limit",
		...options
	}),
	r2: (options) => ({
		type: "r2",
		...options
	}),
	secret: () => ({ type: "secret" }),
	secretsStoreSecret: (options) => ({
		type: "secrets-store-secret",
		...options
	}),
	sendEmail: (options) => ({
		type: "send-email",
		...options
	}),
	stream: (options) => ({
		type: "stream",
		...options
	}),
	text: (value) => ({
		type: "text",
		value
	}),
	vectorize: (options) => ({
		type: "vectorize",
		...options
	}),
	versionMetadata: () => ({ type: "version-metadata" }),
	vpcService: (options) => ({
		type: "vpc-service",
		...options
	}),
	vpcNetwork: (options) => ({
		type: "vpc-network",
		...options
	}),
	worker: (options) => ({
		type: "worker",
		...options
	}),
	workerLoader: () => ({ type: "worker-loader" })
};
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
const triggers = {
	fetch: (options) => ({
		type: "fetch",
		...options
	}),
	queue: (options) => ({
		type: "queue",
		...options
	}),
	scheduled: (options) => ({
		type: "scheduled",
		...options
	}),
	email: (options) => ({
		type: "email",
		...options
	}),
	connect: (options) => ({
		type: "connect",
		...options
	})
};
function durableObject(options) {
	return {
		type: "durable-object",
		...options
	};
}
function worker(options = {}) {
	return {
		type: "worker",
		...options
	};
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
const exports = {
	durableObject,
	worker
};
/** Add a config type while preserving its value, promise, or function shape. */
function addConfigType(config, type) {
	function addType(value) {
		return {
			...value,
			type
		};
	}
	if (typeof config === "function") return (ctx) => {
		const result = config(ctx);
		return result instanceof Promise ? result.then(addType) : addType(result);
	};
	return config instanceof Promise ? config.then(addType) : addType(config);
}
/** Create a type-safe config helper for a particular export type. */
function createConfigDefiner(type) {
	function define(config) {
		return addConfigType(config, type);
	}
	return define;
}
/** Declare a Container application. */
const defineContainer = createConfigDefiner("container");
const defineWorker = createConfigDefiner("worker");
/**
* Declare shared settings.
* Authored as a named `settings` export.
*/
const defineSettings = createConfigDefiner("settings");

//#endregion
//#region src/experimental-config/wrangler-definition.ts
function defineWranglerConfig(config) {
	return config;
}

//#endregion
export { bindings, defineContainer, defineSettings, defineWorker, defineWranglerConfig, exports, triggers };
//# sourceMappingURL=experimental-config.mjs.map