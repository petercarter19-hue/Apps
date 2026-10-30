# todo-panel

A Claude Code mod that keeps a to-do list in a side panel. The list is saved, so it is still there in your next session.

- **Add:** `/todo buy milk`, or ask Claude: "add 'review the PR' to my to-do list".
- **Tick off:** click an item in the To-do panel, or `/todo done 2`.
- **Untick or delete:** `/todo undo 2`, `/todo remove 2`.
- **Tidy up:** `/todo clear` (or the "Clear ticked" button) deletes ticked items.
- **See it:** `/todo` prints the list and opens the panel; `/todo help` lists the commands.

Claude can read and add to the list only through two small tools this mod provides, and only adds when you ask it to.

## Install

At the prompt of a Claude Code terminal session:

```
/plugin install todo-panel --marketplace petercarter19-hue/apps
```

From a shell:

```
claude plugin install todo-panel --marketplace petercarter19-hue/apps
```

In the desktop app: click + next to the prompt box, then Plugins, Add marketplace with `petercarter19-hue/apps`, then Add plugin.
