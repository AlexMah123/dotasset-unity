# DotAsset for Unity

Headless and in-editor asset operations for scripts and AI agents: export, create, edit, migrate and audit Unity assets through one command line, `Scripts~/dotasset.py`.

Open `Window > DotAsset` for the queue state and run logs, and its **Copy dotasset.py** button to put the wrapper at the project root.

With nobody at the machine, `dotasset.py --launch` opens the project in the editor, `--open-scene <path>` opens scenes in it, and `--quit` closes it again (only an editor this caller started).

With `com.unity.pipeline` 0.9 or later installed, the Unity CLI reaches the same runs: `unity command dotasset run --name <RunName> --args "..."`.

Documentation: the `Docs/` folder of the repository, starting with `Docs/AI-Guide.md`.
