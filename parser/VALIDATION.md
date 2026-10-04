# Parser validation / recovery notes

BDO Loot Tracker keeps normal monster **Ground Loot** parsing separate from Gathering support.

## Ground Loot parser

The active parser profile defines:

- BDO server / relay port handling
- 3-byte little-endian application packet framing
- loot signature and signature offset
- item ID / quantity offsets
- minimum packet length
- positive Ground Loot fingerprint checks

Official and Community parser profiles can update independently from the desktop application release.

## Manual Calibration

**Settings → Network → Manual Calibration** performs one guided Ground Loot capture.

Collect normal ground loot only during this short capture. Do not use Storage, Market, Maid or perform unrelated inventory transfers.

The calibration process can rediscover the server port, signature, item/quantity offsets and the positive Ground Loot fingerprint. A successful result can be activated locally and rolled back to the last-known-good profile.

Calibration is intentionally independent from the user-facing **Garmoth-only loot filter** and uses a conservative known-loot anchor set to avoid unrelated inventory traffic winning the candidate search.

## Community parser

When a valid local calibration differs from the current official parser, the desktop app can submit only the normalized parser structure and validation statistics to the built-in Community Parser service. Raw packet captures are not uploaded by the calibration wizard.

Clients can discover an approved Community candidate while waiting for the next official parser profile. A later official profile supersedes the temporary Community candidate automatically.

## Gathering parser

Gathering does **not** use the calibrated Ground Loot packet family.

The dedicated Gathering result packet currently uses:

- 3-byte little-endian packet length at `+0`
- signature `93 14 01` at `+3`
- 15-byte header
- 222-byte result entries
- item ID at entry `+0` (`uint32 LE`)
- quantity at entry `+4` (`uint64 LE`)

Keeping Gathering parsing separate prevents Gathering support from weakening or changing the proven Ground Loot calibration path.
