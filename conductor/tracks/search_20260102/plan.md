# Plan: File Name Search

## Phase 1: Search UI & Basic Filtering (Current Directory)
This phase focuses on adding the visual elements and implementing client-side filtering for files already loaded in the current view.

- [x] Task: Create `SearchViewModel` (or equivalent) and basic search properties [ae91cd3]
    - [x] Subtask: Write tests for `SearchViewModel` logic (query update, filter predicate).
    - [x] Subtask: Implement `SearchViewModel` with `SearchQuery` property and `IsRecursive` toggle.
- [x] Task: Integrate Search Bar into UI [b745206]
    - [x] Subtask: Add `TextBox` for search to `MainWindow` or `ExplorerControl`.
    - [x] Subtask: Bind `TextBox` text to `SearchViewModel.SearchQuery`.
    - [x] Subtask: Implement `Ctrl+F` keyboard shortcut to focus the search box.
- [ ] Task: Implement Filter Logic for File List
    - [ ] Subtask: Write tests for filtering collection view based on search query.
    - [ ] Subtask: Update `FileExplorerViewModel` to filter the `FileList` `CollectionView` based on the search query.
- [ ] Task: Conductor - User Manual Verification 'Search UI & Basic Filtering (Current Directory)' (Protocol in workflow.md)

## Phase 2: Recursive Search Implementation
This phase implements the backend logic for searching through subdirectories via ADB.

- [ ] Task: Implement Recursive Listing/Search in Service Layer
    - [ ] Subtask: Write integration tests (or mocked unit tests) for `AdbService` recursive file listing.
    - [ ] Subtask: Implement `FindFilesRecursiveAsync` in `AdbService` using `ls -R` or similar ADB commands (handling depth/performance).
- [ ] Task: Connect Recursive Search to UI
    - [ ] Subtask: Write tests for `SearchViewModel` handling async search results.
    - [ ] Subtask: Update `SearchViewModel` to trigger `FindFilesRecursiveAsync` when `IsRecursive` is true and query is not empty.
    - [ ] Subtask: Display search results in the main file list (or a dedicated search results view).
- [ ] Task: Handle Search State and Cancellation
    - [ ] Subtask: Add loading indicator logic to ViewModel.
    - [ ] Subtask: Implement `CancellationToken` support to stop search if query changes or user cancels.
- [ ] Task: Conductor - User Manual Verification 'Recursive Search Implementation' (Protocol in workflow.md)
