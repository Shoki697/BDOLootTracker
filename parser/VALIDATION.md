# Parser validation — 2026-09-03 BDO patch

The built-in `EU-2026.09.03.1` profile was derived from the post-maintenance Wireshark capture supplied during development.

Observed BDO connection stayed on TCP server port `8889`.

The profile decoded the seven visible loot events from that capture in order:

| Item ID | Quantity | Visible event |
|---:|---:|---|
| 1 | 490 | Silver x490 |
| 44187 | 2 | Elaborate Metal Part x2 |
| 1 | 461 | Silver x461 |
| 44187 | 1 | Elaborate Metal Part x1 |
| 1 | 1038 | Silver x1,038 |
| 1 | 1021 | Silver x1,021 |
| 757470 | 1 | Grunil Defense Gear Box x1 |

All seven candidates used:

- 3-byte little-endian application packet length
- packet length 254 bytes in the sample
- signature `1E 16 01` at offset 3
- item ID at offset 28
- quantity at offset 32

The original full `.pcapng` is intentionally not bundled with the dev ZIP because it is large and contains raw network traffic.
