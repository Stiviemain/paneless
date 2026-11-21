# Contributing to Paneless

Thanks for wanting to help! Bug reports, ideas and pull requests are all welcome.

## Reporting bugs

Open an issue with the **Bug report** form. The most useful reports include:

- the Paneless version (window title) and Windows version;
- the device model and Android version;
- what you did, what you expected, and what happened;
- the relevant log from `%TEMP%\paneless`: one per mirror window, plus `errors.log` and `on-connect.log`.

**Check logs before posting them.** They can contain device names, IP addresses and serial numbers. Never post unlock codes or anything from `unlock-codes.json`.

Security problems: please don't open a public issue. See [SECURITY.md](SECURITY.md).

## Suggesting features

Open an issue with the **Feature request** form. Describe the situation you're in and what you'd like to happen. "I have 20 test phones and want to…" helps more than a finished design.

## Pull requests

1. For anything bigger than a small fix, open an issue first so we can agree on the approach.
2. Fork, then create a branch from `main`.
3. Keep changes focused. Follow the existing style (see [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md#style)).
4. Check your change:
   - `dotnet build` with no warnings;
   - the smoke tests that need no device: `dotnet run --project tests/Paneless.Smoke -- --groups`;
   - for UI changes, a `--demo --snapshot` of the affected window;
   - if you have a device, the device tests that apply.
5. Add a line to the top section of `CHANGELOG.md`.
6. Open the pull request and fill in the template.

By contributing, you agree that your contributions are licensed under the project's [MIT License](LICENSE).

## Code of conduct

Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
