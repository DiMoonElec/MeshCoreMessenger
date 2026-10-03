# Native viewport audit

Requires a desktop/display session. Uses production Avalonia controls and a freshly
created temporary SQLite fixture through the Desktop test helpers. No real transport,
user data folder or development fixture folder is opened. Not part of headless xUnit
discovery; this diagnostic executable references the test project deliberately.

```sh
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release
```

Fails if the previously first-visible message moves more than 1 logical pixel (DIP),
or if an intermediate frame advances unread. Runs forward pagination with/without
the 500 DTO trim, and backward pagination, in Light/Dark with variable-height text.
Also checks cancellation when a restoring viewport becomes hidden.

`--measure-only` prints measurements without asserting the thresholds, useful before
a change. The test harness temporarily suppresses viewport reporting/detaches the
scroll adapter **only during fixture positioning**, using reflection; the measured
load/restoration runs the normal production adapter and real layout. A fake read
store records all advance attempts; real history reads use SQLite.


D5 send audit uses the production composer/message controls with a simulated UI
message service and temporary SQLite. Core tests separately run the production
message service/client/transport against the loopback Companion emulator.

```sh
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --send-only
```

Checks Light/Dark at 420/960 pixels, explicit selection among two channel slots,
Enter sending exactly once, Shift+Enter and IME preedit sending zero times,
post-commit AcceptedByNode bubble and revision-safe draft clearing. Screenshots
are saved under `$TMPDIR/meshcore-d5-send/`. No hardware node or radio is used.


Public chat switching regression (native ChatsView/ListBox, simulated sends,
temporary SQLite):

```sh
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --switch-only
```

Runs A send → select B → B send → select A → A send in Light/Dark without
incoming notifications during the scenario. Also fails on logged exceptions,
including off-thread command availability notifications.
