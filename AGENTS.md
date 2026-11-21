# Windows Startup Verification

- Run startup-related verification only on a disposable GitHub-hosted Windows VM, including tests with mocked startup data.
- Use the hosted Windows workflow for tests or benchmarks that exercise the complete leftover scanner because it reads startup entries. Use the default verification or `--verify-ui` for local UI and sorting checks.
- Keep startup fixtures and cleanup tests away from the workstation's startup entries.
