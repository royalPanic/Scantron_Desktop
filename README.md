# Scantron Desktop

**Scantron Desktop** is the Windows companion app for
[Scantron Container Inventory](https://github.com/royalPanic/Scantron) — the Android warehouse
handheld. It is where inventory work actually happens: a desk-side editor for container tags,
locations and item rows, plus the sync engine that reconciles what an operator types here with
what a scanner records out in the aisle.

The two apps exchange **one file format** and nothing else. A document exported here is the exact
document the handheld imports, so a file copied over USB and a file pushed over Wi-Fi are
interchangeable by construction.

---

## 📑 Table of Contents

1. [Quick start](#-quick-start)
2. [What this app is for](#-what-this-app-is-for)
3. [The three-way merge](#-the-three-way-merge)
4. [Item identity and merge keys](#-item-identity-and-merge-keys)
5. [LAN transfer hub](#-lan-transfer-hub)
6. [Project structure](#-project-structure)
7. [Tech stack](#-tech-stack)
8. [Building, running and testing](#-building-running-and-testing)
9. [Keyboard shortcuts](#-keyboard-shortcuts)
10. [Where state lives on disk](#-where-state-lives-on-disk)
11. [Troubleshooting](#-troubleshooting)
12. [License](#-license)

---

## 🚀 Quick start

```bash
git clone https://github.com/royalPanic/Scantron_Desktop.git
cd Scantron_Desktop
dotnet build Scantron.Desktop.slnx
dotnet test Scantron.Desktop.slnx
dotnet run --project Scantron.Desktop
```

Requires the **.NET 8 SDK** or newer. The app targets `net8.0-windows` with WPF, so it builds and
runs on Windows only.

---

## 💡 What this app is for

The handheld is fast for scanning and bad for bulk editing. The desktop is the reverse. This app
takes the two seriously as different tools rather than trying to be both.

| Task | Use |
| :--- | :--- |
| Add, edit and re-categorise container tags and locations | Desktop |
| Bulk-correct quantities, categories and notes | Desktop |
| Scan a barcode and fold it into an existing row | Handheld |
| Reconcile edits made on both sides | Desktop, via the merge |

> ⚠️ **The handheld's `ExportImportManager` is the authority on the file format.** Every quirk the
> device has is reproduced deliberately here and commented where it lives. Before changing anything
> in `Scantron.Core`, read the comment on the type you are touching — several of the rules look like
> bugs and are not.

---

## 🔀 The three-way merge

`InventoryMerger.Merge` is the heart of the app. It merges *local* and *remote* against a shared
*base* snapshot, and it is a three-way merge for a specific reason: **"changed" is undefined without
a base**. Given only two documents you cannot tell a deliberate edit from a row nobody touched.

Three design decisions carry the weight:

**Field-level, not row-level.** If the handheld changed `quantity` and the desktop changed
`category`, both wins are correct and neither is a conflict. Warehouse conflicts are overwhelmingly
quantity-versus-metadata, so this is the single highest-value behaviour in the app.

**Conflicts are reported, never auto-resolved.** A true conflict is one where both sides changed the
same field to different values. Picking either one silently loses stock, which is the exact failure
this design exists to prevent. Each one surfaces in the conflict view for a human to settle, and
until it is settled the merge is not accepted.

**Clock skew is flagged, not trusted.** Each device stamps `updatedAt` with its own wall clock. If
the handheld is an hour out, naive last-write-wins would discard real edits. The merge still runs
field-by-field, but sets `ClockSkewDetected` when any timestamp is more than
`InventoryMerger.MaxPlausibleSkew` (10 minutes) from the local clock, so the UI can warn that
recency-based reasoning is unreliable.

On first sync there is no base. Every difference is then reported as a conflict rather than guessed
at — which is why `WorkspaceStore` keeps the base document as carefully as the working one.

---

## 🪪 Item identity and merge keys

From format version **1.1** every item carries a `uuid`, and that supersedes all heuristics. For
legacy `1.0` documents, `ItemKeyResolver` reproduces the device's `addItemMerging` rules so both
sides fold a row into the same existing one:

| Key kind | Matched on | Mirrors |
| :--- | :--- | :--- |
| `Uuid` | `uuid` | `assignMissingItemUuids` |
| `ContainerBarcode` | container + barcode | `getItemByBarcode` |
| `NameOnly` | container + lowercased name, **only against rows with no barcode** | `getNameOnlyItemByName` |

Two rules look odd and are reproduced on purpose:

- **A barcode is scoped to its container** — the same barcode legitimately exists in two containers.
- **An item that _has_ a barcode is never matched by name**, because that barcode is its identity.

Collisions resolve the way the device resolves them: a duplicate `uuid` is re-minted so both rows
survive as separate items, while heuristic collisions resolve to the newest `updatedAt` (the device's
item queries all order `updatedAt DESC` before `LIMIT 1`). Matching that tie-break is what stops the
two sides oscillating on different rows on every sync.

---

## 📡 LAN transfer hub

An HTTP endpoint on port **8756** that the handheld connects to:

| Endpoint | Method | Purpose |
| :--- | :--- | :--- |
| `/health` | `GET` | Probe. Returns the `scantron-hub/1` protocol token and this PC's name. |
| `/pull` | `GET` | Handheld fetches the desktop's current document. |
| `/push` | `POST` | Handheld sends its export; the desktop merges it. |

**The desktop hosts and the handheld connects.** A CK65 is battery-powered and cannot be relied on to
hold a listening socket while docked or asleep, while the desk PC is on and already on the network.
There is no discovery: the operator reads the IP address off the screen and types it into the
handheld by hand, so that address gets a row of its own in the toolbar rather than being truncated
beside the sync state.

Bodies are the **raw export document** — no envelope, no base64, no zip. Both sides already have a
parser and a validator for exactly this document, so an envelope would be a second place to keep in
step and a second way for the halves to disagree. `/push` acknowledges with
`{"ok":true,"containers":N,"items":M}`; error bodies are plain text, because the handheld surfaces
them verbatim in a toast and an operator standing in an aisle can act on a sentence but not on a
JSON error envelope.

Some deliberate choices worth knowing before modifying this code:

- **`TcpHttpListener`, not `HttpListener`.** A non-loopback `HttpListener` prefix needs a URL ACL
  reservation that only an elevated process can make. Without one the hub could bind nothing but
  `127.0.0.1` and then *advertise a LAN address it was not listening on* — the desktop would report
  "sharing" and the handheld could not connect. Binding all interfaces as an ordinary user is the
  only way "sharing" can actually mean reachable.
- **Inert at startup.** Nothing listens until the operator asks for it. An endpoint left open on a
  shared warehouse network is a way for any device on that network to read the day's stock count and
  overwrite it. It stops the moment sharing is switched off.
- **`/pull` refuses rather than serving an empty document.** The handheld imports by clearing and
  replacing, so an empty `containers` array is not an empty document — it is an instruction to delete
  everything the device holds.
- **16 MB body cap** (`HubEndpoints.MaxBodyBytes`), enforced on raw bytes as they arrive. This is an
  unauthenticated endpoint; without the cap a malformed request is a way to exhaust the memory of the
  machine holding the stock count.

`HubEndpoints` is a pure function — `(method, path, body) → (status, contentType, body)` — so the
whole contract, failure paths included, is covered by tests without binding a port.

---

## 📁 Project structure

```
Scantron_Desktop/
├── Scantron.Core/                      net8.0, no UI dependency
│   ├── Models/                          Container, Item, InventoryDocument, InventoryFormat
│   ├── Identity/ItemKeyResolver.cs      Merge keys mirroring the handheld's rules
│   ├── Merge/InventoryMerger.cs         The three-way merge
│   └── Serialization/                   InventoryReader, InventoryValidator, DTOs
│
├── Scantron.Desktop/                    net8.0-windows, WPF
│   ├── App.xaml(.cs)                    Entry point, unhandled-exception logging
│   ├── Mvvm/                            ObservableObject, RelayCommand
│   ├── Models → ViewModels/             MainViewModel and friends
│   ├── Services/                        WorkspaceStore, InventoryFileService, ConflictResolver, Log
│   │   └── Transfer/                    TransferHub, TcpHttpListener, HubEndpoints, InboxStore
│   ├── Themes/                          Material 3 resources
│   └── Views/MainWindow.xaml            The single window
│
├── Scantron.Core.Tests/                 52 tests
└── Scantron.Desktop.Tests/             106 tests
```

`Scantron.Core` deliberately has no WPF dependency, and `MainViewModel` contains no dialogs or file
pickers — commands take a path and report failures through `StatusMessage`, so the entire sync
workflow is drivable from a test.

---

## 🔧 Tech stack

| | |
| :--- | :--- |
| Language | C# 12 |
| UI | WPF, Material 3 (`Themes/MaterialTheme.xaml`) |
| MVVM | Hand-rolled `ObservableObject` / `RelayCommand` |
| Serialization | `System.Text.Json` with a source-generated context |
| Networking | `TcpListener` (no ASP.NET Core, no external dependency) |
| Tests | xUnit 2.5.3, `Microsoft.NET.Test.Sdk` 17.8.0, coverlet 6.0.0 |
| Solution format | `.slnx` |
| Target | `net8.0` (Core), `net8.0-windows` (Desktop) |

No NuGet runtime dependencies. The whole app is BCL plus xUnit in the test projects.

---

## 🔨 Building, running and testing

```bash
# Build everything
dotnet build Scantron.Desktop.slnx

# Run the tests (158 total)
dotnet test Scantron.Desktop.slnx

# Run just the domain logic
dotnet test Scantron.Core.Tests/Scantron.Core.Tests.csproj

# Run the app
dotnet run --project Scantron.Desktop

# Release build
dotnet build Scantron.Desktop.slnx -c Release
```

The solution builds clean with **`TreatWarningsAsErrors`** on, nullable reference types enabled, and
zero warnings.

> ⚠️ **Do not add `InvariantGlobalization` to the WPF project.** WPF resolves the thread culture
> through ICU when building its default text and number formatters, and throws
> `Cannot find non-neutral culture related to 'en-us'` on every layout pass without it. The cost is a
> larger executable; the alternative is an app that throws continuously.

---

## ⌨️ Keyboard shortcuts

This is a data-entry tool, so the keyboard path has to work without reaching for the mouse.

| Key | Action |
| :--- | :--- |
| `Ctrl+N` | New empty document |
| `Ctrl+O` | Open an export file |
| `Ctrl+S` | Export to a file |
| `Ctrl+I` | Import and merge |
| `Ctrl+D` | Add container |
| `Ctrl+P` | Push to handheld |
| `Ctrl+Enter` | Accept the pending merge |

`Ctrl+P` is deliberately **not** `Ctrl+S`. Exporting to a file and publishing to the handheld are
different acts, and an operator who means one should never silently get the other.

---

## 💾 Where state lives on disk

Everything sits beside the executable — `AppContext.BaseDirectory`, which in a normal build is
inside the git-ignored `bin/`. That is why none of these appear in `.gitignore`: they are never
sitting in the working tree.

| Path | Contents |
| :--- | :--- |
| `workspace.current.json` | The document you are editing |
| `workspace.base.json` | Last mutually-agreed snapshot — the merge's third input |
| `inbox/` | Staged inbound pushes (newest 20 kept) |
| `scantron.log` | Tiered log, one line per event, includes pid |

Three properties worth preserving in any change:

- **Both workspace files are written in the device's own export format** and parsed by
  `InventoryReader`. The store holds no DTO mapping of its own, which is what stops it drifting out
  of step with what an export would produce — and it means you can inspect either file in any text
  editor.
- **Every write is staged in a temporary and then moved into place**, so a crash mid-save leaves the
  previous good copy rather than a truncated one. Without that, a power cut during a sync would be
  indistinguishable from a corrupted inventory.
- **A corrupt file is reported, never deleted.** The operator may still be able to salvage an export
  from it, and a sync tool that quietly discards inventory is worse than one that complains.

Exported inventory files (`scantron_inventory*.json`) are git-ignored as user data. The golden
fixture under `Scantron.Core.Tests/Fixtures` **is** tracked — it is an input to the round-trip test
and the suite fails on a fresh clone without it.

---

## 🩺 Troubleshooting

**"Not sharing: ..." appears and nothing listens.**
The hub never throws for a bind failure — a refusal to bind is the single most likely thing to go
wrong (usually a second copy of the app already holding port 8756). The reason is in the toolbar.
Close the other instance, or check `scantron.log`.

**The handheld cannot connect to the address on screen.**
Confirm both are on the same network and that Windows Firewall is not blocking inbound traffic on
port 8756. The address shown is what the hub actually bound, not a guess.

**`/pull` answers "Nothing to send - open a document on the desktop first."**
Intentional. The handheld imports by clearing and replacing, so serving an empty document would
delete everything it holds. Open a document, then try again.

**A merge reports conflicts on rows you already settled.**
The base snapshot did not advance. This is the first-sync failure mode: without a base, no difference
can be told apart from a conflict. Complete a successful sync to promote the base.

**A merge succeeds but the status warns about clock skew.**
One device's clock is more than 10 minutes off. The merge still ran field-by-field and nothing was
auto-discarded, but fix the clock before trusting recency-based decisions.

**A crash dialog says details were written to `scantron.log`.**
That is the intended path. Unhandled UI exceptions are logged and marked handled rather than killing
the process, because losing an in-progress edit to a modal dialog is worse than continuing with one
bad interaction. Send the operator that file.

---

## 📄 License

This project is licensed under the MIT License.

> ℹ️ Neither this repository nor the Android app currently has a `LICENSE` file on disk. Add one to
> match.