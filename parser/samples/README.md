# Parser packet samples

This folder is reserved for packet captures used when a BDO patch changes more than the JSON parser profile can describe.

Recommended workflow:

1. Capture only your own BDO session traffic with Wireshark/Npcap.
2. Add the new `.pcapng` sample here (or host it as a release asset if it becomes too large for the repository).
3. Update `parser/manifest.json` with `sampleVersion`, `sampleUrl`, and the file's SHA-256.
4. `Settings -> Network -> Diagnostics` will detect that a newer sample is available. `Auto Repair` can download/cache the sample for diagnostics.

A new sample is diagnostic input, not executable code. The application never executes code from the parser repository.
