# Contributing to ChaptarrNG

Thanks for your interest in contributing to ChaptarrNG.

## How can you contribute?

### 1. Report Bugs
- Use the GitHub Issues tab
- Search existing issues first to avoid duplicates
- Include:
  - Clear description of the problem
  - Steps to reproduce
  - Expected vs actual behavior
  - Logs (System → Logs → Files in the UI; `/config/logs/` in Docker)
  - Your environment (OS, Docker or built from source, version)

### 2. Suggest Features
- Open a GitHub Issue with the [FEATURE] tag
- Describe the use case
- Explain how it benefits audiobook/eBook management or SeerrNG integration
- Be open to discussion and feedback

### 3. Submit Code
- Clone the repository; fork it only if you do not have write access
- Work on `main` when current GitHub rules allow direct pushes; otherwise create a feature branch
  (`git checkout -b feature/amazing-feature`)
- Make your changes
- Test thoroughly
- Open a pull request only when current GitHub rules require one; push directly to `main` when they allow it

### 4. Improve Documentation
- Fix typos, clarify instructions
- Add examples
- Document undocumented features
- Translate into other languages

## Development setup

### Prerequisites
- .NET SDK compatible with the repo targets (currently `net10.0`)
- Node.js + Yarn (see `package.json` `volta` section for recommended versions)
- FFmpeg and FFprobe on `PATH` when running or testing ChaptarrNG's native media features
- Git

### Building ChaptarrNG

```bash
# Clone ChaptarrNG
git clone https://github.com/snapetech-org/chaptarrng.git
cd chaptarrng

# Install frontend dependencies
yarn install

# Build the UI bundle
yarn build

# Build the backend
dotnet build src/Chaptarr.sln -c Release

# Run (development output)
dotnet _output/net10.0/Chaptarr.dll
```

If you prefer `dotnet publish`:
```bash
dotnet publish src/NzbDrone.Console/Chaptarr.Console.csproj -c Release -o _output/publish
dotnet _output/publish/Chaptarr.dll
```

### Frontend-only development
```bash
# From repo root
yarn install

# Watch rebuilds
yarn watch
```

### Unraid package synchronization

After cloning, run `scripts/install-package-sync-hook.sh` once. The post-commit
hook mirrors committed files from `packaging/unraid/` into the sibling
`chaptarrng-unraid` checkout and pushes the package update to its `main` branch.
Set `CHAPTARRNG_UNRAID_REPO` if that checkout is elsewhere.

### Development Tips
- Frontend code is in `/frontend` (React)
- Backend code is in `/src` (C#/.NET)
- Database migrations are in `/src/NzbDrone.Core/Datastore/Migration/`
- API endpoints are in `/src/Chaptarr.Api.V1/`

## Code style

### C# / .NET
- Follow existing code style
- Use meaningful variable names
- Add XML documentation for public methods
- Keep methods focused and small

### JavaScript / React
- Use functional components where possible
- Follow existing component patterns
- Use PropTypes for type checking
- Keep components focused

## Testing

- Test your changes locally first
- Include unit tests for new functionality
- Ensure existing tests pass
- Test with both audiobooks and eBooks

## Submitting pull requests

1. **Before submitting:**
   - Keep the branch current with `main` only when current GitHub rules require it, using rebase or merge
   - Merge after the checks and approvals required by current GitHub rules are satisfied
   - Update documentation if needed

2. **PR Description should include:**
   - What changed and why
   - Any breaking changes
   - Screenshots for UI changes
   - Testing performed

3. **After submitting:**
   - Respond to any review feedback and make requested changes
   - An explicit maintainer direction to merge is sufficient; no additional review or approval is required beyond current GitHub rules. Use any merge method enabled in GitHub

## Questions?

- Ask in GitHub Discussions or on Discord

## Code of conduct

Please note that this project is released with a [Contributor Code of Conduct](CODE_OF_CONDUCT.md). By participating in this project, you agree to abide by its terms.

## Thank you

Every contribution, no matter how small, helps improve ChaptarrNG and its SeerrNG integration. We appreciate your time and effort!
