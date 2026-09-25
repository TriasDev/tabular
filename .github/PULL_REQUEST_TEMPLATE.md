## What and why

<!-- What changes, and the problem it solves. Link the issue: Fixes #… -->

## Checklist

- [ ] A test that failed before the change and passes after it (for a fix or a new behaviour).
- [ ] `dotnet build` and `dotnet test` pass on net8.0 and net10.0 with zero warnings.
- [ ] If the read path changed: measured, and the numbers are in the description.
- [ ] If an error code was added or changed: `ErrorCodes` and the guide's error-code table agree.
- [ ] Public API changes are described, and breaking ones are called out.
- [ ] No third-party package in `src/`.
