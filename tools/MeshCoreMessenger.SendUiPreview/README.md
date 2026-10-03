# D1 Send UI preview

Production `MessageView`, `ComposerView`, shared styles and theme resources in a
separate in-memory application. It does not start Desktop DI, open SQLite/user
data paths or construct any client/session/transport. Send stays disabled.

```sh
dotnet run --project tools/MeshCoreMessenger.SendUiPreview -c Debug
```

Use Light/Dark, resize (420/960 DIP are the audited widths) and switch the public/
private tabs. Drafts are independent demo text in memory. Byte counts are fixed
examples, not a validator. Buttons change the explanatory line or leave only the
counter. Context menus copy original text, show details and demonstrate independent
hidden/disabled/enabled retry states. Cancel leaves the bubble unchanged; confirmed
mock retry updates its metadata and retains its ID, without adding another row.

Private shows all eight states; channel never claims Delivered. `3/5` and heard
relays are presentation samples, not an implemented retry or relay tracking policy.

Requires a desktop/display session. Automatic native checks:

```sh
dotnet run --project tools/MeshCoreMessenger.SendUiPreview -c Debug -- --audit
```

Checks both themes/widths/tabs, bindings/resources, metadata placement, direction,
menu availability, disabled Send, actual retry handlers cancel/confirm and dialog
theme. Writes PNGs only to the OS temp directory `meshcore-d1-preview` and exits.
Human visual/keyboard acceptance is recorded separately in
[D1](../../docs/messenger/plan/d1-send-ui.md).
