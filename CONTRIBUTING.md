# Contributing to Chaptarr

Thanks for your interest in contributing to Chaptarr.

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
- Explain how it benefits audiobook/eBook management
- Be open to discussion and feedback

### 3. Submit Code
- Fork the repository
- Create a feature branch (`git checkout -b feature/amazing-feature`)
- Make your changes
- Test thoroughly
- Submit a Pull Request

### 4. Improve Documentation
- Fix typos, clarify instructions
- Add examples
- Document undocumented features
- Translate into other languages

## Development setup

### Prerequisites
- .NET SDK compatible with the repo targets (currently `net10.0`)
- Node.js + Yarn (see `package.json` `volta` section for recommended versions)
- Git

### Building Chaptarr
```bash
# Clone your fork
git clone https://github.com/<your-username>/Chaptarr.git
cd Chaptarr

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
   - Rebase on the latest `develop` branch
   - Ensure all tests pass (`dotnet test src/Chaptarr.Core.Test/Chaptarr.Core.Test.csproj`)
   - Update documentation if needed
   - One feature/fix per PR

2. **PR Description should include:**
   - What changed and why
   - Any breaking changes
   - Screenshots for UI changes
   - Testing performed

3. **After submitting:**
   - Respond to code review feedback
   - Make requested changes
   - Be patient - reviews take time

## Questions?

- Ask in GitHub Discussions or on Discord

## Code of conduct

Please note that this project is released with a [Contributor Code of Conduct](CODE_OF_CONDUCT.md). By participating in this project, you agree to abide by its terms.

## Thank you

Every contribution, no matter how small, helps make Chaptarr better for everyone. We appreciate your time and effort!
