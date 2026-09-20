# Security and sensitive reports

Ember inspects the local game process and exports diagnostic data. Security-relevant areas include worker launch and process identity, journal/recovery paths, export filenames, dependency handling, report redaction, and rendering untrusted strings in the offline viewer.

## Reporting

Use [GitHub's private vulnerability reporting](https://github.com/bbauti/ember/security/advisories/new) **if it is enabled**. Do not put credentials, private recordings or an undisclosed exploit in a public issue.

If the private form is unavailable, open a minimal issue titled “Private security contact requested” without vulnerability details or attachments. The maintainer can arrange a private channel. No dedicated security email address or response-time guarantee is currently established.

A useful private report includes the affected version, reproduction steps, expected impact, relevant source locations and a minimal synthetic example where possible. Please distinguish a privacy leak or executable-content issue from incorrect performance interpretation.

## Maintenance scope

Security fixes target the latest release and the current `main` branch. Older versions have no guaranteed backport support. This is a community project without a formal security audit or support SLA. See [AI development disclosure](AI_USAGE.md) and [verification history](docs/verification.md).

## Handling diagnostic data

- Reports are saved locally; there is no automatic upload.
- Logs and user-entered markers can contain identifying information. Redaction is heuristic, not a guarantee of anonymity.
- Optional mod-configuration collection is disabled by default.
- Review files before sharing. Prefer a minimal reproduction or a scoped selection rather than a complete session bundle.
- Never commit raw recordings, game account files, saves, proprietary game assemblies or credentials to this repository.

See [data and sharing semantics](docs/metrics-and-data.md#redaction-and-sharing).
