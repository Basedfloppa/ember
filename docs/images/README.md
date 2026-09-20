# Viewer screenshots

These are actual screenshots of Ember's offline viewer, not generated concept art or mockups. They show the **1.1.2 viewer** displaying a **historical 1.1.0, 30-minute loopback multiplayer test recording**. That test deliberately injected stalls and allocation pressure. It is not a representative overhead benchmark or evidence that a particular mod caused a problem.

| Image | Viewport | Contents |
| --- | --- | --- |
| [investigate-desktop.png](investigate-desktop.png) | 1440 × 1050 | Capture context, incident queue, timeline, filters, flame graph and method/evidence inspector. |
| [investigate-mobile.png](investigate-mobile.png) | 390 × 844 | Responsive navigation, incident selection and time-range controls. |

Both images were captured on 2026-09-19 by `viewer/review.cjs` using headless Chromium. They were copied from the recording-first review batch without altering measurements. The visible views were inspected before publication; they contain no displayed credentials, player names, server address or local filesystem paths. The raw capture is intentionally excluded from the repository.

The UI and its documentation are part of Ember's MIT-licensed project. Game/framework names and method signatures shown in diagnostic output identify their respective software; no game artwork or game binaries are included here. See [third-party notices](../../THIRD_PARTY_NOTICES.md).

## Reproduce with your own recording

```text
node viewer/review.cjs /path/to/report.json
```

The current real-report review script requires Windows, a local Chrome installation and a report with the expected incident-rich test shape. See [testing](../testing.md) for its assumptions. For deterministic synthetic regression coverage, use `node viewer/verify.cjs` instead.
