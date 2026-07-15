# macOS Porting Status

## Stage 0: Uno Desktop Spike

Started on 2026-07-14.

### Scope

- [x] Pin an Uno SDK compatible with .NET 10.
- [x] Add an isolated macOS project without modifying the Windows app project.
- [x] Create the first local-directory browsing slice.
- [x] Verify `osx-arm64` restore, build and process launch.
- [x] Verify `osx-x64` build output.
- [x] Exercise a 10,000-item directory and record list virtualization measurements.
- [ ] Port the existing shell controls one at a time and complete the compatibility matrix.
- [x] Add the first native macOS bridge for Quick Look, Trash and Finder reveal.

### Initial compatibility notes

| Area | State | Notes |
| --- | --- | --- |
| .NET 10 | Ready for build verification | Local SDK 10.0.201 is available |
| Uno Skia Desktop | In progress | SDK pinned to 6.5.36; macOS-only project added |
| Shared build properties | Workaround applied | The repository's custom `TargetFrameworkVersion=net10.0` conflicts with an SDK-reserved property; the spike clears it locally so the SDK can infer the correct value |
| arm64 build | Passed | Debug build completed with zero warnings and zero errors; the app process launched on macOS |
| x64 build | Passed | Cross-build completed with zero warnings and zero errors |
| Debug remote control | Follow-up | A direct CLI launch logs an invalid Uno dev-server endpoint; it does not prevent the app process from starting |
| Uno HotDesign incremental generation | Workaround documented | After broad source edits the cached `ServerProcessorGenerator` can report `Sequence contains no elements`; recreating the macOS project's `obj` directory and restoring produces clean arm64/x64 builds |
| CommunityToolkit.Mvvm | In progress | Used by the first view model through the Uno MVVM feature |
| Basic WinUI XAML | In progress | Grid, buttons, text box, progress ring and ListView used in the shell |
| `ItemsWrapGrid` | Replaced | The Uno Desktop analyzer rejects it; the macOS grid now uses `ItemsView` with `UniformGridLayout` and an `ItemContainer` template |
| Large-directory virtualization | Passed 10,000-item runtime check | At the default window size the grid realized 49 containers and details realized 16; both selection round trips passed |
| Large-selection commands | Passed 10,000-item runtime check | Select-all plus invert-selection completes in 202.5 ms in the virtualized grid and 571.0 ms in details; an Uno `ItemsView` selection-range adapter avoids the 34.6-second per-item path |
| Directory enumeration | Optimized and benchmarked | Cached `FileSystemInfo` metadata and a single collection replacement reduced cold enumeration from 257.7 ms to 38.4 ms, hot enumeration from 74.8–81.6 ms to 27.0–28.5 ms, and cold managed allocation from about 8.1 MB to 5.53 MB in the repeatable 9,500-file/500-directory fixture |
| Existing Files controls | Partially reproduced | The macOS shell now uses one vector system across tabs, navigation, commands, sidebar/footer, file fallbacks, empty/search states and status views, alongside the Files-style address bar, responsive toolbar, sortable grid/details layouts, preview pane and per-tab dual-pane browsing; shared Windows-only controls are still being evaluated individually |
| WinUI Community Toolkit/Labs | Not tested | Each package requires a build and behavior check |
| Win2D and WinUIEx | Not expected to be portable as-is | Replace or isolate before macOS use |
| Windows Shell/COM/CsWin32 | Not portable | Keep in the Windows platform implementation |
| macOS native integration | In progress | Open, Finder reveal, Quick Look, system thumbnails, Trash, FSEvents, native folder picking and persisted security-scoped access grants are bridged; broader Finder extensions remain |
| Native bridge arm64/x64 | Passed | Both dylibs build with the expected Mach-O architecture and exported C ABI |
| Native `.app` packaging | Passed locally | Self-contained arm64 and x64 Release bundles include branded `.icns`, standard plist metadata, embedded .NET runtime, hardened-runtime entitlements and ordered nested-code signing; both bundles pass strict verification and launch on Apple Silicon (x64 via Rosetta) |
| Quick Look thumbnails | Passed | Native smoke test generated a PNG thumbnail from the repository screenshot |
| System Trash | Passed native/history smoke tests | The native bridge returns Finder's authoritative resulting URL; uniquely named temporary files passed move, undo, redo, partial-batch recovery and cleanup checks |
| Managed copy/move engine | Passed initial smoke tests | Recursive copy, relative symbolic links, keep-both naming, replace, skip, move and cancellation cleanup were exercised in an isolated temporary tree |
| File coordination | Passed isolated transaction smoke tests | Root copy operations coordinate source reads and destination writes; moves coordinate both write URLs; system-provided paths feed the existing staging/commit transaction, with callback failures and cancellation preserving rollback behavior |
| Finder metadata preservation | Passed isolated smoke tests | File, directory and symbolic-link extended attributes, real Finder tag payloads and ACL entries survived both initial copy and atomic replacement through the production transfer engine |
| macOS file pasteboard | Build verified | File URL read/write and cut markers are bridged through `NSPasteboard`; live clipboard mutation was intentionally not automated |
| Recursive name/content search | Passed isolated smoke tests | Case-insensitive nested filename and text-content matching, chunk-boundary matching, hidden filtering, cancellation and symbolic-link cycle avoidance were exercised in a temporary tree |
| Spotlight search | Passed indexed/content/filter/fallback smoke tests | Scoped `NSMetadataQuery` searches names and indexed content, translates extension/kind/size/date filters to native predicates, and merges recursive local results under the same parsed query plan |
| Incremental and live search | Passed isolated smoke tests | Recursive results arrive in 32-item batches, Spotlight and fallback batches deduplicate in the view model, and recursive directory monitoring schedules a safe rerun when content changes during or after a search |
| Native directory monitoring | Passed direct/recursive smoke tests | Per-pane monitoring uses an architecture-matched FSEvent stream with file events, root watching, canonical real paths, rebind-safe native lifetimes and the existing 250 ms managed debounce; direct and deeply nested temporary-file changes were observed without startup delay |
| Finder tag search | Passed native/indexed/fallback smoke tests | `tag:` groups map to `kMDItemUserTags`, Finder resource tags are read for fallback validation, comma-separated tags are ORed and repeated tag filters are ANDed |
| Settings and workspace persistence | Passed isolated and runtime restore tests | Versioned atomic JSON preserves preferences, language, recent paths, search history/saved searches, every open window and its tabs, pane paths, active window/tab/pane, window frames/maximized state, split ratios, view, sort state, sidebar visibility/width/collapsed sections, preview visibility and access bookmarks; schema v11 caps restored windows/tabs, migrates older state, validates language and section IDs, clamps restored values, normalizes duplicate grants and restricts the settings file to user read/write |
| Folder access grants | Passed native/lifecycle smoke tests | The sidebar opens `NSOpenPanel`, stores security-scoped bookmarks, restores access before workspace tabs, refreshes stale bookmarks, skips invalid grants, supports revocation, and remaps favorites, saved-search roots and pane descendants when a bookmarked folder moves |
| System sharing | Build verified | Selected file URLs open an `NSSharingServicePicker` anchored to the app window |
| Network servers | Build and validation verified | Normalized SMB/AFP/NFS addresses use NetFS with the macOS credential UI, FTP uses the system handler, recent addresses persist without credentials, and mounted paths feed the existing `/Volumes` sidebar discovery; a live remote-server acceptance pass remains |
| Outbound file drag | Managed pipeline passed | Uno's deferred `StorageItems` provider returned the expected local file URL path; Finder mouse interaction remains an explicit manual acceptance check |
| ZIP archives | Passed initial smoke tests | Round trip, empty directories, Unix modes, relative symbolic links, unique naming, Zip Slip/escaping-link rejection and cancellation cleanup passed in a temporary tree |
| Per-tab dual-pane browsing | Passed build and lifecycle smoke tests | Each tab owns independent primary/secondary navigation state and split ratio, commands route to the active pane, and disposal now cancels queued navigation/search/thumbnail work before directory-monitor callbacks can re-enter a detached tab; the arm64 app remained alive through split-view close and window-transfer cycles without background disposal exceptions |
| Bulk rename and operation history | Passed isolated transaction smoke tests | Multi-selection rename preserves file extensions, assigns stable numeric suffixes, stages all sources before commit, rejects parent/descendant batches, and supports undo/redo; new empty files and folders also have guarded undo/redo |
| Copy/move operation history | Passed isolated transaction smoke tests | Transfer results retain exact committed paths; copy and move replace preserve and swap displaced items, move history restores exact names across different source parents, redo conflicts preserve staging, partial cancellation retains undoable completed-root mappings, and multi-group history failures roll completed groups back; an atomic artifact journal removes crash leftovers on the next launch |
| Trash operation history | Passed native/history smoke tests | Confirmed Trash operations record the system-assigned destination URL, restore items to their exact original paths on undo, refresh changed Trash URLs on redo, retain completed mappings after a partial initial batch, and roll a partial redo back |
| Permanent deletion | Passed isolated/runtime smoke tests | Shift+Delete, the context menu and native Option-Command-Delete use a non-default destructive confirmation; duplicate parent/child roots collapse safely, directory links are removed without following their targets, and partial failures report exactly which earlier roots were deleted |
| Finder metadata and security properties | Passed native/runtime round trips | The polished, localized single-item properties dialog groups general and sharing details; it edits owner/UID, group/GID, POSIX ACL text, validated Unix modes, Finder tags, and hidden/locked flags. Native identity resolution, non-empty ACL write/clear, immediate hidden filtering and cross-field transactional rollback passed on a temporary file |
| Navigation and preview UI | Passed algorithm/runtime layout checks | Home-relative and volume-root breadcrumbs preserve exact target paths, Ctrl+L editing and Escape round-trip visibility, sidebar sections collapse/restore, and the persisted preview pane reserves content width without breaking 10,000-item details virtualization |
| Responsive command and empty-state UI | Passed build/runtime checks | Nineteen localized command labels now pair with crisp font-independent vector icons; retuned wide, overflow and compact tiers preserve access without horizontal clipping, and runtime diagnostics passed all three breakpoints plus empty/search states |
| View and sidebar interaction fidelity | Passed build/runtime checks | Vector tab/navigation controls, 11 semantic sidebar icons, four footer actions, file/folder fallbacks, empty/search illustrations and status view toggles replace mixed Emoji/symbol fonts; sort, view and persisted sidebar interactions passed runtime round trips |
| English/Simplified Chinese localization | Passed resource/runtime checks | English and `zh-Hans` resource key sets match; System mode maps every Chinese locale (including `zh-SG`) to Simplified Chinese and other locales to English, while Settings also offers explicit choices; both launches rendered localized icon labels plus navigation tooltips/accessibility names and all 11 sidebar labels |
| Sidebar navigation fidelity | Passed runtime round trips | The longest matching sidebar path follows the active pane (for example Downloads takes precedence over Home), section headers expose vector disclosure indicators, Favorites/Recent/Libraries/Network/Drives collapse independently, and section state persists through schema v11 |
| Open With integration | Passed native/runtime enumeration checks | Launch Services supplies the compatible application list with the default first; item context and native File menus open a localized chooser, an AppKit panel can select another `.app`, and both native entry points are architecture-matched |
| Recent locations | Passed isolated persistence/runtime checks | Successful navigation records up to eight non-built-in folders, the collapsible sidebar group avoids duplicates with libraries and mounted volumes, a visible footer action clears history, and schema v11 normalizes invalid section state and duplicate paths |
| Native macOS menus, windows and shortcuts | Passed build/runtime checks | Localized application, File, Edit, View, Go and Window menus route through the active window with live validation snapshots; a transparent full-content AppKit title bar places the Files tab strip beside the standard traffic-light controls and disables duplicate system tabs. New Window and Close Window use conventional Command shortcuts, up to eight windows restore their independent tab/dual-pane workspaces, frame/maximized state and prior active-window ordering across launches, off-screen frames are constrained to the current display, concurrent preference saves merge without clobbering newer values, and 18 Command/Command-Shift/Command-Option accelerators coexist with the portable Control bindings |
| Duplicate and open-in-new-tab workflows | Passed isolated/runtime round trips | Command-D and context/native menus create localized, uniquely numbered copies through the transactional transfer engine with Finder metadata and Undo/Redo preserved; Command-Return opens one selected folder in an independent active tab and tab disposal passed lifecycle checks |
| Tab management and closed-tab recovery | Passed full-state runtime round trips | Tabs can be dragged to reorder, duplicated beside the source with Command/Control-Shift-K, moved intact to a new native window, closed to the left/right or closed except for the selected tab through localized context and native File menus. Up to 20 closed tabs reopen with Command/Control-Shift-T; primary/secondary paths, active pane, split ratio, grid/details mode and sorting restore together in nearest-tab order, and reordered/transferred window workspaces are persisted without treating a move as a close |
| Symbolic-link creation | Passed file/directory/broken-link history smoke tests | Context/native menus and Command-Option-L create localized, uniquely numbered relative symbolic links in one rollback-safe batch; Undo validates every link before deleting any, Redo refuses conflicts, and changed-link protection leaves the batch untouched |

### Current vertical slice

`Files.App.MacOS` currently provides:

- Home, back, up and refresh navigation.
- Address entry with validation and error reporting.
- Asynchronous, cancellable local directory enumeration.
- Directory-first, culture-aware sorting.
- Independent tabs with their own navigation state.
- Folder context menus and Command-Return open a selected directory in a new active tab without disturbing the original tab's navigation state.
- Tabs drag to reorder, duplicate beside their source, or move with complete dual-pane state into a newly created native window; localized tab context and native File menus close left/right/other tabs or reopen the latest of 20 closed tabs. Command/Control-Shift-K duplicates and Command/Control-Shift-T recovers.
- Optional per-tab dual-pane browsing with independent paths, histories, searches, sort modes and grid/details layouts.
- Active-pane routing for navigation, selection, file commands, drag/drop, keyboard shortcuts and the shared address/status controls.
- Localized native macOS application, File, Edit, View, Go and Window menus with enabled-state validation and conventional Command shortcuts routed to the active window and pane.
- Independent macOS windows through File > New Window / Command-N, with Command-Shift-W close behavior, active-window menu routing, retained window lifetimes and schema-v11 restoration of every window's tab/dual-pane workspace, active-window index, AppKit frame and maximized state.
- A unified transparent macOS title bar that preserves the native traffic-light controls while moving the Files tab strip into the former empty title region, with a reserved 76-point control inset and no duplicate AppKit tab bar.
- A draggable dual-pane divider with minimum-width enforcement, responsive window resizing and per-tab ratio memory.
- A Files-style tab strip, address bar, locations sidebar, command toolbar, content card and status bar.
- Folder-icon tab headers with truncated long names, clickable details columns and visible ascending/descending sort indicators in both panes.
- Custom flat navigation/toolbar states avoid native gray disabled blocks; font-independent vector artwork now covers primary commands, tabs, navigation, sidebar/footer actions, file fallbacks, empty/search illustrations and status toggles without missing-glyph boxes.
- A retuned three-tier responsive command toolbar that moves secondary actions—and then rename, share and delete—into a synchronized More menu before icon-and-label content can clip.
- Home-relative/volume-root breadcrumb navigation with click-to-edit behavior, Ctrl+L focus, Escape cancellation and horizontal overflow handling.
- A collapsible and draggable-width sidebar grouped into Favorites, Libraries, Network and Drives, with active-path highlighting, persisted visibility/width/section state and Files-style footer actions.
- Virtualized grid and details layouts with extended selection, selection count and size; a 10,000-item fixture realizes only the visible containers.
- Synchronized compact grid/details controls in the status bar, preserving selection and active-pane routing when switching layouts.
- Centered empty-folder and no-search-results visuals in both primary and secondary panes, with localized guidance and loading-state suppression.
- Double-click directory navigation.
- Localized create-folder and rename operations with input validation and conflict errors.
- Transactional multi-selection rename with preserved extensions, conflict numbering, rollback, case-only rename support and guarded undo/redo for rename and newly created items.
- Undo/redo for copy, move and Trash—including mixed replace/non-replace roots, keep-both destination names, multiple original parent folders and roots completed before cancellation—with rollback of already completed history groups; internal staging and replacement backups stay out of browsing and search results.
- Select all, invert selection and copy-path commands across grid/details views, toolbar, context menus and macOS keyboard shortcuts.
- Double-click file opening, Finder reveal, Space-bar Quick Look and confirmed move-to-Trash operations backed by authoritative resulting URLs for exact undo/redo.
- Launch Services-backed Open With selection from item and native File menus, including the default/compatible applications and an AppKit picker for another application.
- Strongly confirmed permanent deletion through Shift+Delete, Option-Command-Delete and item context menus, with parent/child normalization, link-safe behavior and partial-batch error reporting.
- Multi-selection copy, cut and paste with an app-local clipboard.
- Command-D and item context menus make transactional, metadata-preserving duplicates with localized names, conflict numbering and Undo/Redo.
- Windows Create Shortcut is mapped to rollback-safe relative symbolic links for files and folders, including localized naming, broken-target handling and guarded Undo/Redo.
- Two-phase copy/move staging, recursive progress reporting, cancellation and cleanup of incomplete staging items.
- Root-level `NSFileCoordinator` transactions for copy, move and replacement so iCloud/File Provider presenters can coordinate access before staging and commit.
- Per-operation conflict handling: keep both with an automatic suffix, replace or skip.
- Preservation of relative symbolic links, modification timestamps and Unix permission modes during managed transfers.
- Preservation of ACLs plus macOS extended attributes—including Finder tags, FinderInfo, resource forks and symbolic-link attributes—during copy/move staging and replacement.
- Finder-compatible file URL exchange through the macOS pasteboard, including reading files copied by other apps.
- Per-tab back/forward navigation stacks and name/date/size sorting in both directions.
- Debounced, cancellable Spotlight name/content search scoped to the active folder, merged incrementally with a bounded recursive text scan for unindexed and hidden files; composable `ext:`, `kind:`, `tag:`, `size:` and `modified:` filters share native and fallback semantics.
- Search results appear in progressive batches with an in-progress count, and recursive directory monitoring reruns an active query after nested changes.
- Single- and multi-selection properties with recursive size/count, timestamps, Unix modes and symbolic-link targets; the single-item sharing section edits owner/UID, group/GID and POSIX ACL text with permission-aware native errors.
- Editable single-item Finder tags, validated Unix permissions, and Finder-compatible hidden/locked flags in the localized properties dialog, with cross-field transactional rollback if any metadata save fails.
- A persisted right-side preview pane with live thumbnail/glyph, name, path, modified time, size, multi-selection summary and a Quick Look action.
- Persisted system/light/dark theme, hidden-file visibility and default new-tab view settings.
- Automatic English/Simplified Chinese selection from the macOS locale, plus explicit System, English and Simplified Chinese choices in Settings (restart required after changing language).
- Debounced atomic workspace persistence for tab selection, pane paths, active pane, split ratios, grid/details view and sorting, with missing-path fallback during restore.
- Search history plus named saved searches that retain both query and root folder, exposed through the integrated search menu.
- System sharing through `NSSharingServicePicker` and a unified right-click command menu.
- Asynchronous Quick Look thumbnails in grid and details layouts with bounded concurrency and a glyph fallback.
- Debounced per-tab native FSEvents monitoring for automatic refresh after local changes, including recursive live-search refresh.
- Copy-safe file URL drops from Finder/other apps into the active folder, routed through the same conflict and progress engine.
- A Files-style New menu for folders and text documents, with extension-aware conflict naming.
- Outbound file dragging through Uno's deferred macOS file URL provider.
- ZIP creation and extraction with progress/cancellation, staging cleanup, path traversal protection and safe symbolic-link restoration.
- Persisted sidebar favorites plus mounted system/external volumes discovered from `/` and `/Volumes`.
- A persisted, collapsible recent-locations sidebar group with built-in/volume deduplication, eight-item recency ordering and an explicit clear action.
- Native folder selection with persisted security-scoped bookmarks, pre-workspace access restoration, moved-folder path migration and grant revocation in Settings.
- Connect-to-server UI for SMB, AFP, NFS and FTP, with secure system credential handling, recent-server persistence and mounted-volume navigation in the active pane.
- Opening the current or selected folder directly in Terminal.app through `NSWorkspace`.
- Self-contained, architecture-specific `Files.app` Release packages with native bundle metadata, branded icon, embedded runtime and configurable ad-hoc/Developer ID signing.

Outbound file URL dragging now has a verified managed data-provider path, but still needs a Finder-to-app manual interaction pass. Network URL validation, secure NetFS mounting and recent-server state are implemented, but SMB/AFP/NFS authentication and reconnect behavior still require acceptance testing against real servers. Every open window's tabs, dual-pane paths, active panes, ratios, views and sorting now restore across launches through versioned atomic settings; security-scoped access is activated before that restore and follows moved bookmark targets. The managed transfer engine combines root-level `NSFileCoordinator` transactions with staging, rollback, ACLs and extended-attribute preservation; live iCloud/File Provider contention remains an acceptance-test requirement. Copy and move history journal displaced destinations for replace undo/redo and roll partially completed replay groups back; Trash history uses the native resulting URL to restore exact original paths and refreshes that URL after redo. Spotlight-backed folder search covers indexed document content, progressive fallback batches, composable metadata/Finder-tag filters, nested-change refresh, result locations, history and named saved searches; broader AQS aliases remain follow-up search work. Local arm64/x64 app bundles are valid and runnable; Developer ID signing, notarization, stapling, update delivery and optional universal-binary assembly remain credentialed release gates.
