# Build Skill

## How to Build

First, locate the solution file:

```bash
find . -name '*.slnx' -o -name '*.sln' | head -3
```

Then build:

```bash
dotnet build <solution-file>
```

If the build fails, read the error messages carefully and fix the issues before proceeding.
