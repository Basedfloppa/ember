# AI development disclosure

**Ember Diagnostics was written entirely with AI assistance, using OpenAI GPT-6 Astra through OpenCode.** This applies to the C# mod, recording worker, analysis/export code, HTML/CSS/JavaScript viewer, tests and project documentation. AI involvement was central to implementation, not limited to autocomplete or proofreading.

## Who did what

- **bbauti** initiated the project, defined its purpose and requirements, chose the design direction, authorized development and maintains the public repository.
- **GPT-6 Astra** generated and revised the implementation, tests and documentation, and used development tools to build, run tests, inspect results and iterate on the viewer. Additional AI-agent review sessions were used during development.
- **spark and Lithos Probe** were the principal motivations for the project. Their authors are credited in the [README](README.md#motivation-and-credits) and [third-party notices](THIRD_PARTY_NOTICES.md). They do not endorse or maintain Ember.

This disclosure does not claim that every line has been audited by a human. An independent agent review is still an AI review, not an independent human security or correctness audit. Historical references to a “reviewer” or “finish review” in the design records refer to AI-agent review unless explicitly stated otherwise.

## What validation means

The project includes executable regression tests, browser interaction checks and documented local gameplay captures. [Verification history](docs/verification.md) identifies the versions and environments tested. Passing tests demonstrate the scenarios they exercise; they do not establish universal compatibility, zero overhead, complete privacy redaction or correct causal attribution.

Source, test fixtures, build instructions and known limitations are available so other people can inspect and challenge the implementation. Please report reproducible problems or propose corrections through [Issues](https://github.com/bbauti/ember/issues) and pull requests. The maintainer is responsible for what is accepted and published, regardless of which tool produced it.

## AI is not a runtime dependency

Ember does not call an AI service, require an AI account/API key, or automatically upload recordings. Its incident analysis is local code. The optional `GPT-REPORT.md` handoff is a local file that a user may choose to share with an external tool. The offline HTML viewer and other exports can be used without AI.

## Contributions

AI-assisted contributions are welcome under the same review and testing expectations as any other contribution. Describe material AI involvement in the pull request, inspect the diff, and provide reproducible validation. Do not describe generated tests or another model's approval as human review. See [CONTRIBUTING.md](CONTRIBUTING.md).
