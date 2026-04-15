# Cmd Batcher

A Windows desktop tool for running many command-line tasks side by side. Define a set of commands once, organize them into groups, and fire them off in parallel with one click &mdash; each with its own working directory, live output, and stdin channel.

Built for build pipelines, content uploaders, and any workflow where you'd otherwise be juggling half a dozen terminal windows.

## Features

- **Parallel execution** &mdash; run all commands in a group (or across all groups) simultaneously.
- **Per-command state** &mdash; each row shows idle / running / done / error, exit code, and elapsed time.
- **Live output panel** &mdash; click any command to see its streaming stdout/stderr.
- **Interactive stdin** &mdash; send input to a running process from the app (useful for prompts and passwords).
- **Groups** &mdash; organize commands into collapsible groups (e.g. one group per project or platform).
- **Persistent presets** &mdash; label, folder, and command are saved automatically between sessions.
- **Dark UI** with a compact, dense layout tuned for many rows.

## Requirements

- Windows 10 / 11
- [.NET 6 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/6.0) (or SDK to build from source)

## Building

```bash
dotnet build -c Release
```

The executable lands in `bin/Release/net6.0-windows/CmdBatcher.exe`.

To run from source during development:

```bash
dotnet run
```

## Usage

1. **Launch** `CmdBatcher.exe`.
2. **Add a group** with the `+ Add Group` button in the top bar, or use the default group that appears on first launch.
3. **Add commands** to a group. For each command, fill in:
   - **Label** &mdash; a short name shown in the row and output header.
   - **Dir** &mdash; the working directory the command runs from (use the `...` picker if you like).
   - **Cmd** &mdash; the command line to execute (e.g. `build.bat 1.2.3`, `npm run deploy`, `python script.py`).
4. **Run** a single command with its row's Run button, or use **Run All** in the top bar to launch every command across every group.
5. **Stop** individual commands from their row, or **Stop All** to terminate everything at once.
6. **Click a row** to focus its output in the right-hand panel. Use **Refresh** to re-read, **Clear** to wipe the display.
7. **Send stdin** by typing into the input box below the output panel and pressing Send (or Enter) &mdash; the text is written to the focused process's standard input.

Commands, groups, and layout state are saved automatically when the app closes.

## Where settings are stored

User data is stored in:

```
%APPDATA%\ArcenSettings\CmdBatcher\_user_session.json
```

On a typical system this resolves to `C:\Users\<you>\AppData\Roaming\ArcenSettings\CmdBatcher\_user_session.json`. Back this file up to move your presets between machines.

## Tips

- Commands are launched via `cmd.exe /c`, so shell builtins, `.bat` files, and piping all work as expected.
- Output is capped at the most recent 800 lines per slot to keep memory usage bounded on long-running tasks.
- Exit codes are shown next to the elapsed time once a command finishes &mdash; non-zero codes show the row in the error state.

## License

See repository for license details.
