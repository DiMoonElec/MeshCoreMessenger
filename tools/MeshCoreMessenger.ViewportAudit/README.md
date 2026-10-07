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


History clear audit (production conversation menu/dialog, temporary SQLite, offline):

```sh
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --history-clear-only
```

Runs Public/Private × Light/Dark × 420/960: opens the real menu, cancels confirmation,
then confirms deletion, checks empty history and retained draft. Screenshots go to
`$TMPDIR/meshcore-history-clear/`. No hardware or user database is used.
See [verification report](../../docs/testing/history-clear.md) for the actual run
status; the current environment may fail before opening a window with RenderTimer -6661.


macOS startup preflight (no window, display state unchanged):

```sh
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --startup-check-only
```

All modes check CoreVideo display-link availability before Avalonia startup and
report managed startup failures to stderr with exit code 1. See
[display/screen-lock investigation](../../docs/testing/viewport-audit-startup.md).

S1 instance activation audit (production MainWindow, real local IPC/secondary process,
temporary SQLite and fake supervisor):

```sh
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --instance-activation-only
```

Checks visible/minimized/hidden/maximized-then-minimized window activation, focus,
retained selection/history/draft/placement and zero connection commands. The existing
window is reused; pending shutdown rejects activation and a failed attempt permits
it again. No hardware or user database is opened. macOS Dock/Finder reopen
and Windows foreground policy require their separate package/manual checks; see
[S1 report and checklist](../../docs/testing/s1-instance-activation.md).


Private route reset audit (production menu, simulated route service, temporary SQLite):

```sh
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --route-reset-only
```

Checks Light/Dark × 420/960: command once, post-commit flood metadata, unchanged
history/draft. Screenshots: `$TMPDIR/meshcore-route-reset/`. Core tests separately
use production route service/library over loopback TCP. See
[verification report](../../docs/testing/private-route-reset.md).


Private send/resend acceptance (native controls, simulated message service, temporary SQLite):

```sh
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --private-send-only
```

Light/Dark × 420/960: Enter/Shift+Enter/IME, first-send history materialization,
Unconfirmed → real “Отправить еще раз” menu → separate pending bubble,
retained draft and independent late source ACK. Screenshots:
`$TMPDIR/meshcore-d6-private-send/`. Production retry cycles and protocol ACK
matching are verified separately over loopback TCP; see [P8 acceptance](../../docs/testing/private-retries-acceptance.md).
