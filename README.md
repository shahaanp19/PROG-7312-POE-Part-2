# SmartX.Web — IoT Gateway Command Management and Diagnostics

## Overview

SmartX.Web is a web-based application designed to support IoT gateway command management, collection diagnostics, and operational data processing. The application brings together a structured navigation system, advanced data structures, command-state management, and timeline organisation to provide a consistent interface for monitoring and managing command-related information.

This project focuses on the practical application of data structures and reliable frontend navigation within a Blazor web application. Particular attention is given to efficient key-based lookups, ordered command processing, unique state tracking, and diagnostic verification.

## YouTube Demonstration

| Resource | Link |
|---|---|
| Application demonstration video | [Add YouTube link here]() |

## Key Features

### Main Menu Navigation and Active Pillar State

The navigation system provides access to the application's main modules through a consistent menu interface.

- **Overview:** Provides access to the main dashboard.
- **Command Stream:** Provides access to command-stream functionality.
- **Command History:** Provides access to historical command information.
- **Collection Diagnostics:** Provides access to collection verification and diagnostic evidence.
- **Active navigation states:** Visually identifies the currently selected destination.
- **Consistent interface styling:** Applies aligned navigation buttons, spacing, and active-state styling.

The navigation structure is intended to make movement between modules predictable while maintaining a consistent user experience.

### Advanced Collections: Stacks and Queues

The command-management workflow includes diagnostic verification of command ordering and state transitions.

The diagnostic checks cover:

- Priority-based command ordering.
- FIFO ordering among commands with equal priority.
- Priority-aware command processing.
- Undoing the newest pending command.
- Preventing undo operations from affecting already processed commands.
- Critical-alert ingestion.
- Clearing pending queues while preserving command history.

These behaviours are important for managing commands in a predictable order and maintaining the distinction between pending and processed operations.

### Lookup Structures: Dictionaries and Key-Value Maps

Dictionary-based structures support efficient retrieval and organisation of command-related records.

The implementation includes:

- **Command record indexing:** A dictionary maps command identifiers to their corresponding command records.
- **Device-based indexing:** A dictionary organises command records by device identifier.
- **Cached device results:** Device-specific command results can be maintained and updated as commands are added.
- **Key-based retrieval:** Methods such as `TryGetCommand` and `ContainsCommand` provide command lookup by identifier.
- **Sorted command timelines:** A sorted collection supports chronological organisation and deterministic ordering.
- **Timeline queries:** Diagnostic checks cover date-range filtering, device filtering, and result limits.

The collection diagnostics include checks for device dictionary lookup, cache updates after insertion, sorted timeline ordering, and timeline filtering behaviour.

### Unique Collections: Sets

Hash-based sets support membership tracking and help avoid repeatedly storing the same identifier within a tracked collection.

The command-state implementation includes:

- A `HashSet<Guid>` for tracking pending command identifiers.
- A `HashSet<string>` for tracking active errors.
- Membership checks using `Contains`.
- Case-insensitive comparison for active error identifiers.

These structures provide a foundation for efficient membership verification and unique state tracking.

### Collection Diagnostics and Verification

The Collection Diagnostics interface provides a central location for reviewing collection-related checks and their results.

The reported diagnostics include checks for:

- Priority ordering and FIFO behaviour.
- Priority-aware command processing.
- Undo and queue-clearing behaviour.
- Critical-alert ingestion.
- Device dictionary lookup and cache updates.
- Sorted timeline ordering and filtering.

The recorded diagnostic run showed **26 tests passed**. This result documents the reported test run; it should not be interpreted as proof that every rubric requirement or every possible application scenario has been verified.

## Technology Stack

| Technology | Purpose |
|---|---|
| C# | Application logic and data-structure implementation |
| .NET 10 | Application runtime and framework |
| Blazor Web App | Interactive web application |
| Razor components | UI composition and navigation |
| MudBlazor 9.8 | UI component library |
| CSS | Styling, layout, navigation alignment, and responsive presentation |
| Generic collections | Command tracking, lookup, ordering, and membership verification |

## Project Structure

The following files are relevant to the functionality documented in this project.

| File or component | Responsibility |
|---|---|
| `NavMenu.razor` | Main menu links and navigation presentation |
| `CollectionDiagnostics.razor` | Collection diagnostics interface and verification evidence |
| `CommandStateService.cs` | Command-state management, record indexing, pending identifiers, and related state operations |
| `app.css` | Global design tokens, layout styles, navigation buttons, active states, and responsive rules |

The exact responsibilities of individual methods should be confirmed against the current source code.

## Running the Application

### Prerequisites

- .NET 10 SDK
- Visual Studio with Blazor/.NET development support, or another compatible .NET development environment
- The project source code and any required dependencies

### Instructions

1. Clone or download the project repository.
2. Open the solution in Visual Studio, or navigate to the directory containing the project file.
3. Restore the project's NuGet dependencies.
4. Build the solution and resolve any build errors.
5. Run the `SmartX.Web` project.
6. Open the local URL provided by the development environment.
7. Navigate between Overview, Command Stream, Command History, and Collection Diagnostics.
8. Open Collection Diagnostics to review the available collection verification results.

Use the launch profile configured for the project when running locally.

## Verification and Testing

The application's collection diagnostics provide evidence of expected command-processing and collection behaviour.

| Verification area | Example checks |
|---|---|
| Command ordering | Priority ordering and FIFO within equal priority |
| Command processing | Priority-aware `ProcessNext` behaviour |
| Undo handling | Undoing pending commands and skipping processed commands |
| Alert management | Critical-alert ingestion |
| Queue management | Clearing pending commands while preserving history |
| Dictionary lookup | Device dictionary lookup and cache updates |
| Timeline processing | Sorted ordering, date-range filtering, device filtering, and result limits |
| Set membership | Pending-ID and active-error membership checks |

The diagnostic results should be considered together with source-code inspection and, where applicable, independent test execution.


## Future Improvements

Potential areas for further development include:

- Expanding automated tests for command-state transitions and edge cases.
- Measuring lookup and timeline performance with larger datasets.
- Strengthening diagnostic coverage for duplicate alerts and set-based tracking.
- Verifying state preservation and navigation behaviour across all modules.
- Documenting and testing predictive recommendations against actual query-history patterns.
- Improving operational logging and error handling where needed.

## Author and Project Information

| Field | Details |
|---|---|
| Project | SmartX.Web |
| Domain | IoT gateway command management and diagnostics |
| Framework | .NET 10 / Blazor Web App |
| Assessment | Part 2 — Data Structures and Application Functionality |
| Demonstration | [YouTube demonstration link]() |

- Shahaan Pillay - ST10438099
