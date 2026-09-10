# Logic assets

This root is reserved for authoritative game rules, balance values, simulation
data, collision shapes, and navigation data shared by client and server.
No runtime content loader is implemented here.

Logic assets must not depend on presentation assets or presentation-specific
types. Authoring/import metadata belongs outside the shipping runtime contract.
Future load operations must expose measurements and structured status/failure
results under the [engine architecture](../../../../docs/architecture.md).

See [game asset ownership](../README.md) for the complete split.
