# Adapter strategy

Adapters implement the conversion contract and declare explicit, directional format pairs. Desktop composition registers the M0 simulation adapter through dependency injection. Runtime assembly discovery is disabled in M0.

Future adapters should discover only configured executable locations, probe versions with bounded timeouts, verify executable provenance and expose supported pairs at runtime. Options will be mapped to allowlisted structured arguments. Keep external processes behind the process runner with per-tool policy and isolated working directories.

Process isolation, capability metadata, versions and license notices should ship with each reviewed adapter. Changes to the in-process interface require recompiling adapters. An external adapter protocol can be versioned separately when there is a concrete third-party integration.
