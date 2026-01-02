# Specification: File Name Search

## Goal
Implement a search feature that allows users to quickly locate files and folders by name (substring match) within the current directory and optionally within all subdirectories.

## Requirements

### User Interface
- **Search Bar:** A text input field located in the main toolbar or near the file list.
- **Search Scope Toggle:** A checkbox or toggle button to switch between "Current Directory" (default) and "Recursive" search.
- **Search Status:** Visual feedback when a search is in progress (e.g., progress bar or spinner) and when no results are found.
- **Keyboard Shortcut:** `Ctrl+F` should focus the search bar.

### Functional Requirements
- **Filtering (Current Directory):** As the user types, the file list should visually filter to show only items matching the input string.
- **Recursive Search:** When enabled, the search should query the ADB service to find matching files in the current folder and all subfolders.
- **Case Sensitivity:** Search should be case-insensitive by default.
- **Navigation:** Clicking a search result should navigate to that file's location or select it if it's in the current view.
- **Performance:** Recursive search operations must be asynchronous to prevent freezing the UI. Users should be able to cancel a long-running search.

### Technical Considerations
- **ViewModel:** Implement search logic in a dedicated `SearchViewModel` or integrate into the existing `FileExplorerViewModel` (or equivalent).
- **Service Layer:** Extend `AdbService` or `FileSystemService` to support recursive listing or filtering if not already present.
- **Concurrency:** Use `Task` and `CancellationToken` for async search operations.

## User Stories
- As a user, I want to type "camera" to instantly see all files with "camera" in their name in the current folder.
- As a user, I want to find a lost file deep in a folder structure without opening every folder manually.
- As a user, I want to press `Ctrl+F` to quickly start searching without using the mouse.
