# Agent Templates (Defaults)

These `*.agents.md` files are **default templates** shipped with the CopilotHive application.
They are used as fallback instructions when no config repo is configured.

## Runtime Override

At runtime, CopilotHive loads agent instructions from the
[CopilotHive-Config](https://github.com/robkaandorp/CopilotHive-Config) repository.
The config repo's `agents/` folder takes precedence — any matching `*.agents.md` file
in the config repo overrides the local template.

The **Improver** worker autonomously edits the config repo copies based on iteration
metrics, creating a self-improving feedback loop.

## Editing

- To change runtime behavior: edit the files in the **config repo**, not here.
- To change the shipped defaults: edit these files and rebuild the Docker image.
- The `history/` subfolder is used by AgentsManager for version tracking and rollback.
