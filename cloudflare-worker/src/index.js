const OFFICIAL_MANIFEST_URL =
  "https://raw.githubusercontent.com/Shoki697/BDOLootTracker/main/parser/manifest.json";

const KEY_MANIFEST = "community:latest:manifest";
const KEY_PROFILE = "community:latest:profile";
const KEY_META = "community:latest:meta";

const MAX_BODY_BYTES = 64 * 1024;
const MIN_SAMPLE_COUNT = 10;
const MAX_SAMPLE_COUNT = 500;
const MIN_CONFIDENCE = 0.75;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === "GET" && url.pathname === "/health") {
      return json({
        ok: true,
        service: "BDOLootTracker Community Parser",
        transport: "ground-loot-only",
      });
    }

    if (request.method === "GET" && url.pathname === "/community/latest") {
      return getLatestManifest(request, env);
    }

    if (request.method === "GET" && url.pathname === "/community/profile") {
      return getLatestProfile(env);
    }

    if (request.method === "POST" && url.pathname === "/community/submit") {
      return submitCalibration(request, env);
    }

    return json({ success: false, message: "Not found." }, 404);
  },
};

async function getLatestManifest(request, env) {
  const stored = await env.COMMUNITY_PARSER.get(KEY_MANIFEST, "json");
  if (!stored || !stored.candidateVersion) return new Response(null, { status: 204 });

  // Build the profile URL from the current Worker origin so custom domains and
  // workers.dev URLs both work without storing a deployment-specific hostname.
  const origin = new URL(request.url).origin;
  stored.profileUrl = `${origin}/community/profile`;
  stored.sourceIssueUrl = "";

  return json(stored, 200, { "Cache-Control": "public, max-age=30" });
}

async function getLatestProfile(env) {
  const profileJson = await env.COMMUNITY_PARSER.get(KEY_PROFILE);
  if (!profileJson) return new Response(null, { status: 204 });

  return new Response(profileJson, {
    status: 200,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "public, max-age=30",
    },
  });
}

async function submitCalibration(request, env) {
  const contentType = request.headers.get("content-type") || "";
  if (!contentType.toLowerCase().includes("application/json")) {
    return json(fail("Expected application/json."), 415);
  }

  const declaredLength = Number(request.headers.get("content-length") || 0);
  if (declaredLength > MAX_BODY_BYTES) {
    return json(fail("Calibration payload is too large."), 413);
  }

  const raw = await request.text();
  if (new TextEncoder().encode(raw).length > MAX_BODY_BYTES) {
    return json(fail("Calibration payload is too large."), 413);
  }

  let report;
  try {
    report = JSON.parse(raw);
  } catch {
    return json(fail("Invalid JSON."), 400);
  }

  const basic = validateReportShape(report);
  if (!basic.ok) return json(fail(basic.message), 400);

  let official;
  try {
    official = await fetchOfficialSnapshot();
  } catch (error) {
    return json(fail(`Official parser could not be verified: ${safeMessage(error)}`), 503);
  }

  if (report.baseOfficialVersion !== official.manifest.latestProfileVersion) {
    return json({
      success: false,
      accepted: false,
      alreadyCurrent: false,
      officialVersion: official.manifest.latestProfileVersion,
      message:
        "The official parser changed after calibration. Re-run the comparison before submitting.",
    }, 409);
  }

  const candidate = normalizeProfile(report.profile);
  const profileValidation = validateProfile(candidate, true);
  if (!profileValidation.ok) return json(fail(profileValidation.message), 400);

  if (profilesEquivalent(candidate, official.profile)) {
    return json({
      success: true,
      accepted: false,
      alreadyCurrent: true,
      officialVersion: official.manifest.latestProfileVersion,
      message: "A matching official parser is already available. No Community candidate is needed.",
    });
  }

  // If an identical candidate is already published for this exact official base,
  // treat the submission as a harmless duplicate instead of rewriting KV.
  const existingManifest = await env.COMMUNITY_PARSER.get(KEY_MANIFEST, "json");
  const existingProfileJson = await env.COMMUNITY_PARSER.get(KEY_PROFILE);
  if (
    existingManifest &&
    existingProfileJson &&
    existingManifest.baseOfficialVersion === official.manifest.latestProfileVersion
  ) {
    try {
      const existingProfile = JSON.parse(existingProfileJson);
      if (profilesEquivalent(candidate, existingProfile)) {
        return json({
          success: true,
          accepted: false,
          alreadyCurrent: true,
          candidateVersion: existingManifest.candidateVersion || "",
          officialVersion: official.manifest.latestProfileVersion,
          message: "An identical Community parser candidate is already published.",
        });
      }
    } catch {
      // Corrupt old KV data may be replaced by a new validated submission.
    }
  }

  const now = new Date();
  const candidateVersion = buildCandidateVersion(now);
  candidate.profileVersion = candidateVersion;

  // JSON bytes are hashed exactly as stored and served by /community/profile.
  const profileJson = JSON.stringify(candidate, null, 2) + "\n";
  const profileSha256 = await sha256Hex(new TextEncoder().encode(profileJson));

  const manifest = {
    schemaVersion: 1,
    candidateId: crypto.randomUUID(),
    candidateVersion,
    baseOfficialVersion: official.manifest.latestProfileVersion,
    profileUrl: "", // filled dynamically by GET /community/latest
    profileSha256,
    createdAtUtc: now.toISOString(),
    sourceIssueUrl: "",
    notes: `Anonymous Ground Loot calibration: ${report.mobSampleCount} samples, confidence ${Number(report.mobConfidence).toFixed(3)}.`,
  };

  // The Worker intentionally stores only parser data + validation metadata.
  // It does not store account, character, loot history, packet capture or IP data.
  const meta = {
    schemaVersion: 1,
    appVersion: String(report.appVersion || ""),
    sampleCount: report.mobSampleCount,
    confidence: report.mobConfidence,
    groundLootCheckCount: report.groundLootCheckCount,
    baseOfficialVersion: official.manifest.latestProfileVersion,
    candidateVersion,
    createdAtUtc: now.toISOString(),
  };

  await Promise.all([
    env.COMMUNITY_PARSER.put(KEY_PROFILE, profileJson),
    env.COMMUNITY_PARSER.put(KEY_MANIFEST, JSON.stringify(manifest)),
    env.COMMUNITY_PARSER.put(KEY_META, JSON.stringify(meta)),
  ]);

  return json({
    success: true,
    accepted: true,
    alreadyCurrent: false,
    candidateVersion,
    officialVersion: official.manifest.latestProfileVersion,
    message:
      "Calibration accepted. The Community parser candidate is now available to other clients. KV propagation can take a short time.",
  }, 201);
}

function validateReportShape(report) {
  if (!report || typeof report !== "object") return bad("Calibration report is missing.");
  if (report.schemaVersion !== 2) return bad("Unsupported calibration report schema.");
  if (report.calibrationPassed !== true) return bad("Calibration did not pass.");
  if (report.differsFromOfficial !== true) return bad("Report does not claim an official-parser difference.");
  if (typeof report.baseOfficialVersion !== "string" || !report.baseOfficialVersion.trim())
    return bad("Base official parser version is missing.");
  if (!Number.isInteger(report.mobSampleCount) || report.mobSampleCount < MIN_SAMPLE_COUNT || report.mobSampleCount > MAX_SAMPLE_COUNT)
    return bad(`Ground Loot sample count must be between ${MIN_SAMPLE_COUNT} and ${MAX_SAMPLE_COUNT}.`);
  if (typeof report.mobConfidence !== "number" || !Number.isFinite(report.mobConfidence) || report.mobConfidence < MIN_CONFIDENCE || report.mobConfidence > 1)
    return bad(`Ground Loot confidence must be between ${MIN_CONFIDENCE} and 1.0.`);
  if (!Number.isInteger(report.groundLootCheckCount) || report.groundLootCheckCount < 2 || report.groundLootCheckCount > 8)
    return bad("Ground Loot fingerprint must contain 2 to 8 checks.");
  if (!report.profile || typeof report.profile !== "object") return bad("Parser profile is missing.");
  if (!Array.isArray(report.profile.groundLootChecks) || report.profile.groundLootChecks.length !== report.groundLootCheckCount)
    return bad("Ground Loot fingerprint count does not match the report.");
  return { ok: true };
}

function validateProfile(profile, requireGroundLootOnly = false) {
  if (profile.schemaVersion !== 1) return bad("Unsupported parser profile schema.");
  if (requireGroundLootOnly && profile.groundLootOnly !== true)
    return bad("Only Ground Loot allowlist profiles may be submitted.");
  if (!Number.isInteger(profile.serverPort) || profile.serverPort < 1 || profile.serverPort > 65535)
    return bad("Invalid server port.");

  const signatureBytes = hexBytes(profile.signature);
  if (!signatureBytes || signatureBytes.length < 2 || signatureBytes.length > 16)
    return bad("Invalid parser signature.");

  const integerFields = [
    ["signatureOffset", 0, 1024],
    ["packetLengthOffset", 0, 1024],
    ["packetLengthBytes", 1, 4],
    ["maximumPacketLength", 64, 4_000_000],
    ["itemIdOffset", 0, 4096],
    ["quantityOffset", 0, 4096],
    ["minimumLength", 1, 8192],
  ];

  for (const [name, min, max] of integerFields) {
    const value = profile[name];
    if (!Number.isInteger(value) || value < min || value > max)
      return bad(`Invalid ${name}.`);
  }

  if (!Number.isInteger(profile.maxReasonableItemId) || profile.maxReasonableItemId < 1000 || profile.maxReasonableItemId > 100_000_000)
    return bad("Invalid maxReasonableItemId.");

  if (!isSafeIntegerLike(profile.maxReasonableQuantity) || Number(profile.maxReasonableQuantity) < 1 || Number(profile.maxReasonableQuantity) > 1_000_000_000_000)
    return bad("Invalid maxReasonableQuantity.");

  const checks = profile.groundLootChecks;
  if (!Array.isArray(checks))
    return bad("Ground Loot fingerprint has an invalid format.");
  if (profile.groundLootOnly && (checks.length < 2 || checks.length > 8))
    return bad("Ground Loot fingerprint must contain 2 to 8 checks when Ground Loot mode is enabled.");
  if (!profile.groundLootOnly && checks.length > 8)
    return bad("Ground Loot fingerprint has too many checks.");

  let required = Math.max(
    profile.signatureOffset + signatureBytes.length,
    profile.packetLengthOffset + profile.packetLengthBytes,
    profile.itemIdOffset + 4,
    profile.quantityOffset + 8,
  );

  const seenOffsets = new Set();
  for (const check of checks) {
    if (!check || !Number.isInteger(check.offset) || check.offset < 0 || check.offset > 8192)
      return bad("Ground Loot fingerprint has an invalid offset.");
    const bytes = hexBytes(check.bytes);
    if (!bytes || bytes.length < 1 || bytes.length > 16)
      return bad("Ground Loot fingerprint has invalid bytes.");
    if (check.offset + bytes.length > profile.maximumPacketLength)
      return bad("Ground Loot fingerprint exceeds maximum packet length.");
    const key = `${check.offset}:${normalizeHex(check.bytes)}`;
    if (seenOffsets.has(key)) return bad("Ground Loot fingerprint contains duplicate checks.");
    seenOffsets.add(key);
    required = Math.max(required, check.offset + bytes.length);
  }

  if (profile.minimumLength < required)
    return bad("Minimum packet length is smaller than configured parser fields/fingerprint.");

  return { ok: true };
}

async function fetchOfficialSnapshot() {
  const manifestResponse = await fetch(OFFICIAL_MANIFEST_URL, {
    headers: { "User-Agent": "BDOLootTracker-CommunityParser-Worker/0.12.7" },
    cf: { cacheTtl: 0, cacheEverything: false },
  });
  if (!manifestResponse.ok) throw new Error(`manifest HTTP ${manifestResponse.status}`);

  const manifestText = await manifestResponse.text();
  const manifest = JSON.parse(manifestText);
  if (
    !manifest ||
    manifest.schemaVersion !== 1 ||
    typeof manifest.latestProfileVersion !== "string" ||
    typeof manifest.profileUrl !== "string" ||
    typeof manifest.profileSha256 !== "string"
  ) {
    throw new Error("official manifest is invalid");
  }

  const profileResponse = await fetch(manifest.profileUrl, {
    headers: { "User-Agent": "BDOLootTracker-CommunityParser-Worker/0.12.7" },
    cf: { cacheTtl: 0, cacheEverything: false },
  });
  if (!profileResponse.ok) throw new Error(`profile HTTP ${profileResponse.status}`);

  const profileBytes = new Uint8Array(await profileResponse.arrayBuffer());
  const actualHash = await sha256Hex(profileBytes);
  if (actualHash.toLowerCase() !== manifest.profileSha256.trim().toLowerCase())
    throw new Error("official profile SHA-256 mismatch");

  const profile = JSON.parse(new TextDecoder().decode(profileBytes));
  const valid = validateProfile(normalizeProfile(profile), false);
  if (!valid.ok) throw new Error(`official profile validation failed: ${valid.message}`);

  return { manifest, profile: normalizeProfile(profile) };
}

function profilesEquivalent(left, right) {
  const scalarNames = [
    "schemaVersion",
    "region",
    "serverPort",
    "signatureOffset",
    "packetLengthOffset",
    "packetLengthBytes",
    "maximumPacketLength",
    "itemIdOffset",
    "quantityOffset",
    "minimumLength",
    "maxReasonableItemId",
    "maxReasonableQuantity",
    "groundLootOnly",
  ];

  for (const name of scalarNames) {
    const a = name === "region" ? String(left[name] || "").trim().toUpperCase() : left[name];
    const b = name === "region" ? String(right[name] || "").trim().toUpperCase() : right[name];
    if (String(a) !== String(b)) return false;
  }

  if (normalizeHex(left.signature) !== normalizeHex(right.signature)) return false;

  const aChecks = normalizeChecks(left.groundLootChecks);
  const bChecks = normalizeChecks(right.groundLootChecks);
  if (aChecks.length !== bChecks.length) return false;
  for (let i = 0; i < aChecks.length; i++) {
    if (aChecks[i] !== bChecks[i]) return false;
  }

  // Legacy transfer-suppression fields intentionally do not participate once
  // both profiles are Ground Loot allowlists, matching the desktop client.
  if (left.groundLootOnly === true && right.groundLootOnly === true) return true;

  if (left.suppressLookbackBytes !== right.suppressLookbackBytes) return false;
  if (left.suppressStateTimeoutMilliseconds !== right.suppressStateTimeoutMilliseconds) return false;

  const aSuppress = (left.suppressIfPrecededBy || []).map(normalizeHex).filter(Boolean).sort();
  const bSuppress = (right.suppressIfPrecededBy || []).map(normalizeHex).filter(Boolean).sort();
  return JSON.stringify(aSuppress) === JSON.stringify(bSuppress);
}

function normalizeProfile(source) {
  return {
    schemaVersion: Number(source.schemaVersion ?? 1),
    profileVersion: String(source.profileVersion || ""),
    region: String(source.region || "EU").trim().toUpperCase(),
    serverPort: Number(source.serverPort),
    signature: spacedHex(source.signature),
    signatureOffset: Number(source.signatureOffset),
    packetLengthOffset: Number(source.packetLengthOffset),
    packetLengthBytes: Number(source.packetLengthBytes),
    maximumPacketLength: Number(source.maximumPacketLength),
    itemIdOffset: Number(source.itemIdOffset),
    quantityOffset: Number(source.quantityOffset),
    minimumLength: Number(source.minimumLength),
    maxReasonableItemId: Number(source.maxReasonableItemId),
    maxReasonableQuantity: Number(source.maxReasonableQuantity),
    groundLootOnly: source.groundLootOnly === true,
    groundLootChecks: Array.isArray(source.groundLootChecks)
      ? source.groundLootChecks.map((x) => ({
          offset: Number(x.offset),
          bytes: spacedHex(x.bytes),
        }))
      : [],
    suppressLookbackBytes: Number(source.suppressLookbackBytes || 0),
    suppressStateTimeoutMilliseconds: Number(source.suppressStateTimeoutMilliseconds || 0),
    suppressIfPrecededBy: Array.isArray(source.suppressIfPrecededBy)
      ? source.suppressIfPrecededBy.map(spacedHex).filter(Boolean)
      : [],
  };
}

function normalizeChecks(checks) {
  return (checks || [])
    .map((x) => `${Number(x.offset)}:${normalizeHex(x.bytes)}`)
    .sort();
}

function hexBytes(value) {
  const compact = normalizeHex(value);
  if (!compact || compact.length % 2 !== 0) return null;
  const bytes = new Uint8Array(compact.length / 2);
  for (let i = 0; i < bytes.length; i++) {
    const n = Number.parseInt(compact.slice(i * 2, i * 2 + 2), 16);
    if (!Number.isFinite(n)) return null;
    bytes[i] = n;
  }
  return bytes;
}

function normalizeHex(value) {
  return String(value || "").replace(/[^0-9a-fA-F]/g, "").toUpperCase();
}

function spacedHex(value) {
  const compact = normalizeHex(value);
  if (!compact || compact.length % 2 !== 0) return "";
  return compact.match(/.{2}/g).join(" ");
}

function isSafeIntegerLike(value) {
  const n = Number(value);
  return Number.isSafeInteger(n);
}

function buildCandidateVersion(date) {
  const p = (n) => String(n).padStart(2, "0");
  return `COMMUNITY-${date.getUTCFullYear()}.${p(date.getUTCMonth() + 1)}.${p(date.getUTCDate())}.${p(date.getUTCHours())}${p(date.getUTCMinutes())}${p(date.getUTCSeconds())}.${String(date.getUTCMilliseconds()).padStart(3, "0")}`;
}

async function sha256Hex(bytes) {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
  return Array.from(digest, (b) => b.toString(16).padStart(2, "0")).join("");
}

function bad(message) {
  return { ok: false, message };
}

function fail(message) {
  return {
    success: false,
    accepted: false,
    alreadyCurrent: false,
    candidateVersion: "",
    officialVersion: "",
    message,
  };
}

function safeMessage(error) {
  if (error instanceof Error) return error.message;
  return String(error || "unknown error");
}

function json(value, status = 200, extraHeaders = {}) {
  return new Response(JSON.stringify(value, null, 2) + "\n", {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
      ...extraHeaders,
    },
  });
}
