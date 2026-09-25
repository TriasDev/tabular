# Security policy

## Supported versions

Until 1.0, only the latest 0.x release receives fixes.

## What counts as a vulnerability here

TriasDev.Tabular reads files that users upload, so the file is the attack surface. These are
vulnerabilities:

- a file that makes a reader use memory or time out of proportion to its size — a structure without
  a ceiling, a loop that ignores cancellation, an expansion the bounds do not catch;
- a file that crashes the host with an exception the library does not document (anything other than
  `TabularException` and its subclasses, `OperationCanceledException`, or an `ArgumentException` for a
  caller's own mistake);
- a file that is read as data it does not contain — values silently altered, records silently joined
  or dropped — where an attacker could use that to smuggle a value past validation.

The ceilings and what they protect are listed in the guide, under "Bounds".

## Reporting

**Please do not report vulnerabilities in public issues.** Use GitHub's private reporting:
[Report a vulnerability](https://github.com/TriasDev/tabular/security/advisories/new).

Include what the file does, a minimal file or the code that generates one, the version, and what you
observed (memory, time, exception). A generator script is better than the file itself when the file
is large.

We aim to acknowledge a report within a few working days, agree on a fix and a disclosure date with
you, and credit you in the advisory unless you prefer otherwise.
