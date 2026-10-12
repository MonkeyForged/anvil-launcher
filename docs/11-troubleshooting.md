# Troubleshooting

Each section below names what you see, the cause where it is known, and the log line — quoted
as it appears in the log — that confirms it. Where the cause is not certain, that is stated
rather than guessed; check [The log](10-modules-and-log.md#the-log) for the line named.

## Anvil found no games

`Library` shows `No games in the library` instead of a grid of cards.

**Likely cause:** either Steam is not installed where Anvil looks
(`~/.local/share/Steam`), or the library really is empty — a fresh Steam install with
nothing in it yet.

**What to do:** confirm Steam is installed and has at least one game, then press
`Rescan` — see [Rescan](04-library.md#rescan).

**Confirm in the log:** if Steam itself could not be found at all, the log has
`no steam installation found (checked: {Checked})`, with the paths Anvil looked at in
place of `{Checked}`. If Steam was found but the scan still turned up nothing, the log
instead has `library rescanned: 0 games` — Steam is there, its library is just empty.

## Steam did not restart

You pressed `Save`, the bar walked through `Closing Steam…`, `Writing…` and
`Starting Steam…` as usual — see [Save & Restart](03-first-change.md#3-save--restart) —
but Steam's window never came back.

Two different things can cause this, and Anvil tells them apart:

- **Steam could not be closed at all.** Nothing was written, and the save bar says so —
  `Steam could not be closed`, detail `nothing was changed`. The log confirms it with
  `steam could not be closed — transaction aborted, backup intact`.
- **The write itself succeeded, but Steam did not come back up afterwards.** This case is
  silent in the interface: since your change was written, the bar closes exactly like a
  normal, successful save. The only sign is that Steam's window is missing. The log has
  `files written, but steam did not start`. Your change is saved either way — start Steam
  yourself.

Steam checks for updates before it shows a window, which can take a while on a slow
connection; there is no separate log line for "still starting" versus "gave up", only the
one above once Anvil stops waiting.

## The game does not show as running

You started a game, but its card never gets the green band along its footer — or it gets one
and loses it again almost immediately.

Anvil decides "running" from the process tree, with no grace period: a game counts as
running for as long as its process is there, and not an instant longer. **The exact cause
of a specific game not being detected is not established here** — check the log for
`game session started: appid {AppId}, pid {Pid}, {ProcessCount} process(es), launcher
service {LauncherService}` around the time you launched it. If it never appears, Anvil
never saw a process for that app ID. If it appears and is followed almost immediately by
`game session ended: appid {AppId}`, whatever Anvil found exited again right away — for
instance a launcher that starts, hands off to something else, and closes itself.

## There is no tray icon

No icon appears in the system tray, and with `Minimize to tray` on, closing the window
quits Anvil instead of hiding it — see [Minimize to tray](09-settings.md#minimize-to-tray).

**Likely cause:** your desktop environment is not offering a place for the icon. Anvil
asks the session bus for one at startup and again every time the window is hidden or
restored; it does not retry beyond that, and there is nothing to turn on in Anvil itself.

**What to do:** check that your desktop's panel has a system tray / status notifier area
enabled. This is a setting of your desktop environment, not of Anvil.

**Confirm in the log:** at startup, either `started in tray` or `no tray available;
showing window` appears, depending on what Anvil found. If there was no session bus to
ask at all, the log has `no session bus address; assuming no tray` instead; if the bus
was there but did not answer, it has `status notifier watcher not reachable; assuming no
tray`.

## A change did not take effect

You picked a value, but after a save — or after reopening Anvil — it looks like the old
one again.

**Likely cause, most common:** `Save` was never pressed. Picking a value only stages a
pending change — see
[Nothing is written until you press Save](03-first-change.md#read-this-first-saving-closes-steam).
If the save bar was not showing an unsaved game at the time, nothing had been written yet.

**A second, quieter cause:** if the value you picked already matched what Steam had on
disk, Anvil drops the staged change on the spot — there is nothing to save, and the bar
never appears in the first place. The log has `change dropped: appid {AppId} — matches
steam` for this (select `Debug` or `All` on the [Log](10-modules-and-log.md#the-log) page
to see it).

**Confirm in the log:** every actual save attempt logs `save requested: {Count} game(s)`
followed by `save finished: {Outcome}`. No such pair around the time in question means
`Save` was not pressed. An `{Outcome}` of `NothingToDo` means it was pressed, but nothing
differed from what Steam already had.

## Show in Steam did nothing

You right-clicked a game and chose `Show in Steam`, and either nothing visibly happened,
or Steam's window did not come to the front.

If Steam's window did not come forward: this is the well-understood, harmless case
covered in full under
[The context menu](04-library.md#the-context-menu) — with Steam's window closed at the
time, Anvil has nothing to bring forward and logs `no steam window to raise (steam not
running yet?)`, but Steam's page still opens and a newly-opened window takes focus on its
own.

If genuinely nothing happened — no page opened in Steam at all — **the cause is not
established here.** Confirm the click itself registered: the log has `show in steam:
appid {AppId}` for every press of the entry. If that line is present but no window ever
appeared, Anvil handed the request to Steam and does not track what happened after that —
there is nothing further in the log to check.

## A game card shows no Wand chip

You switched Wand Bridge on for a game, but its card in the library carries no
`Wand · ready` chip — see [Module chips](04-library.md#module-chips).

**Likely cause:** something the module needs is not in place yet, so it has nothing to offer
for this game and stays silent on the card rather than showing a chip that promises otherwise.

**What to do:** the reason is written out on the module's own card. Select the game, scroll the
details panel to the `Wand Bridge` card, and read what it says — `The app package is missing`,
a named dependency, or a `not possible` sentence. [What it needs
first](06-wand-bridge.md#what-it-needs-first) covers each one.

**Confirm in the log:** select the category `Modules` in the module dropdown and look for
`module {Key} readiness: {State}` with `wand_bridge` in place of `{Key}`. If the check itself
threw, the line is `module {Key} readiness check failed` instead, with the exception in the
tooltip.

## Attaching Wand or CE appears to do nothing

You pressed the play button on the module's card (or the attach entry in the context menu), the
line `Attaching…` went away after a moment, and nothing else changed: no `Wand · running` or
`CE · running` chip, no green `running` tag on the card, no cyan stop button.

**This is a known gap, not a mistake on your part.** In version 1.3.1 a failed attach has
nowhere to appear in the interface — the card does not turn red, no message is shown, and Anvil
has no notifications at all. The log is the only place it is recorded.

**What to do:** open [Log](10-modules-and-log.md#the-log) and look at the time you pressed the
button. Three categories carry different parts of the story, which is why looking in only one
can leave you with the impression that nothing was written:

| Category | What it holds |
|---|---|
| `Sessions` | the attach itself — `attach run: {Outcome} — {Reason}` and `attach refused: {Outcome} — {Reason}` |
| `Modules` | whether the module was ready at all — `module {Key} attach preparation failed`, or `attach action for {Key} at appid {AppId}: refused — the prefix is not prepared` |
| `wand_bridge` | Wand Bridge's own work, such as unpacking the program |
| `ce_bridge` | CE Bridge's own work, such as copying the chosen version before it starts — `cannot copy {Release} from {Path}` if that fails |

`{Outcome}` names the case: the game was gone before Anvil could attach, the wait ran out, or
the container refused. `{Reason}` is the detail behind it.

**The most common cause worth checking first:** the game has to be running, and it has to have
been started **after** the launch option was saved. A game that was already running when you
pressed `Save` is still running without the service Anvil needs — quit it, start it again, and
try once more.

## The Wand Bridge switch turned itself off

You had Wand Bridge on for a game, and now its switch is off — or you pressed the play button on its
card, nothing attached, and the switch went off.

**Cause:** the game's prefix no longer has the .NET Framework 4.8 that Wand needs. That happens
when the game is reinstalled, when you delete its prefix, or when a Proton update removes it.
Attaching Wand to such a prefix makes Wand fail, so Anvil switches the module off instead. It
checks when it reads your library, and once more right before every attach. A game that is
running is left alone until it ends.

**What to do:** switch Wand Bridge back on for the game and press `Save`. Saving installs what
Wand needs into the prefix again, as it did the first time. If the game is running at that moment,
Anvil does this after the game has ended.

**Confirm in the log:** select the category `Modules`. One of these lines names the game:

| Line in the log | When |
|---|---|
| `module {Key} switched off for app {AppId}: prefix not prepared` | Anvil read your library and found the prefix without .NET |
| `attach action for {Key} at appid {AppId}: refused — the prefix is not prepared` | Anvil was about to attach and found the prefix without .NET |

**If it switches itself off again after every save,** look for this line in the same category:

> `prefix of appid {AppId} has proton version '{Version}', which Anvil cannot place — it does not prepare this prefix`

Anvil could not tell which Proton version built the prefix, so it has no matching package to
install and does not guess. `{Version}` is what it read. This is worth reporting, together with
that line.

## Wand opens on its home page, not on the game

You ticked `Open this game in Wand`, attached, and Wand came up attached — but on its home page
rather than on this game's page.

**Opening on the home page is the fallback, not a failure**, so the interface says nothing about
it. The log says why: select the category `wand_bridge` in the module dropdown and look at the
time you attached.

| Line in the log | What it means | What to do |
|---|---|---|
| `Wand opens on its home page: "Open this game in Wand" is off for {AppId}` | the checkbox is not **saved** on for this game — ticked but not saved counts as off | tick it and press `Save` |
| `Wand catalog: not refreshed, "Look up games online" is off` | the switch in [Settings](09-settings.md#wand-bridge) is off, so Anvil did not go online for the list | turn it on, or accept the home page for games Anvil's copy does not know yet |
| `Wand catalog: no copy, no mapping for {AppId}` | Anvil has no copy of the list yet, and the download did not finish within three seconds | attach again a little later — the download carries on in the background |
| `Wand catalog: no mapping for {AppId}` | Wand's list has no entry for this game | nothing — Wand does not know this game |
| `Wand catalog: {AppId} is ambiguous ({Count} entries), no mapping` | Wand's list names more than one game for this App ID, and Anvil will not guess | pick the game in Wand yourself |

If the download itself went wrong, a warning `Wand catalog: refresh failed, keeping copy` sits
next to one of these lines, with the reason.

**If the log says `Wand opens on the game: {Address}`** and Wand still shows its home page, Wand
was most likely running already when you attached. Anvil does not check whether a running Wand
follows to the new game — detach, make sure Wand has closed, and attach again.

## The module card says "not possible"

The `Wand Bridge` or `CE Bridge` card in the details panel is tagged `not possible`, its switch
and its action button are greyed out, and it carries a sentence below them.

**The sentence is the answer.** The `Wand Bridge` card can show four different ones, each listed
with what to do about it under [Where it does not work](06-wand-bridge.md#where-it-does-not-work).

The `CE Bridge` card can show only one of them, `CE Bridge needs a Proton prefix — this game runs
as a native Linux build.` Cheat Engine needs nothing inside the prefix, so a game that has never
been started, whose platform Anvil cannot tell, or that was moved back to an older Proton version
still gets a usable CE Bridge card — see [Where it does not
work](07-ce-bridge.md#where-it-does-not-work) in the CE Bridge chapter.

Two of Wand Bridge's sentences are worth repeating here because they are fixable:

- `start the game once so Steam creates it` — Steam builds a prefix the first time it runs a
  game, not when it installs one. Start it, quit it, press `Rescan`.
- `This game was moved back to an older Proton version` — the prefix was prepared for a newer
  Proton than the one now set. Below the sentence Anvil prints both names; switch back in Steam,
  or delete the prefix and let Steam rebuild it.

The other two — a native Linux build, and a platform Anvil could not determine — are facts about
the game, not settings. There is nothing to change.

## Wand asks me to log in again

Wand comes up attached, but wants you to log in, and it did not the last time.

Anvil copies Wand's login into the game's container before attaching and copies it back out
afterwards, so a login should survive from one game to the next.

**The cause of a specific case is not established here.** The copy is known to work — it is
what the module was tested on — but the program decides for itself when a stored login is no
longer good enough, and Anvil cannot see that decision.

**What to do:** log in once more. It holds across the following launches. If it asks every
single time, that is worth reporting.

**Confirm in the log:** the copy in each direction logs `{What} {Count} file(s) into {Target}`
under the category `Sessions`, once before attaching and once after detaching. A count of `0`
on either line means nothing was carried, and then the login could not have survived. If the
folder could not be reached at all, the line is `state folder refused: {Outcome} — {Reason}`.

## Get CE 7.7 is greyed out

On the `CE Bridge` card in [Settings](09-settings.md#ce-bridge), `Get CE 7.7` cannot be pressed,
and the sentence below the row reads:

> `Getting CE needs bubblewrap. Install it, or put a complete installation into ce_bridge/.`

**Cause:** `bubblewrap` is not installed. Anvil opens the vendor's installer only inside it, and
without it there is no getting CE through Anvil — see [Without
bubblewrap](07-ce-bridge.md#without-bubblewrap). The sentence is the confirmation; it appears
only when Anvil finds no `bwrap` program.

**What to do:** install `bubblewrap` with your package manager, then switch to another page and
back to `Settings`. Anvil looks for it again each time the settings page is shown, not while the
page stays open. Or skip the download altogether and put a complete installation into
`ce_bridge/` yourself — see [Placing an installation
yourself](07-ce-bridge.md#placing-an-installation-yourself).

## Getting CE failed

You pressed `Get CE 7.7`, and instead of `ready to attach` the row ended with a red sentence
below it and the button back. Whatever the sentence, **nothing was kept**: Anvil removes
everything this attempt fetched and installed. The sentence stays until you press
`Get CE 7.7` again. Pressing `Cancel` is not a failure and leaves no sentence.

| Red sentence | Cause | What to do |
|---|---|---|
| `Could not reach the vendor — check the network connection. Nothing was kept.` | the installer did not arrive: no network, or the connection dropped on the way | check the connection and press `Get CE 7.7` again |
| `The download failed — nothing was kept. See the log for details.` | the download went wrong although the network answered: the vendor's server turned the request down (the address may have changed), or another reason such as a full disk | press `Get CE 7.7` again; if it repeats, the vendor's address may have changed — place an installation yourself; the log line below names the vendor's answer or the error |
| `The installer closed before it finished — nothing was kept.` | the installer window was closed before it had installed the program | press `Get CE 7.7` again and go through the installer to the end |
| `Incomplete build — the 64-bit program is missing. Nothing was kept.` | the installed program lacks its 64-bit part, or carries one of the extra files of the vendor's deliberately crippled edition | press `Get CE 7.7` again; if it repeats, report it together with the log line |
| `Unexpected files — the installed program has files that are not part of CE 7.7. Nothing was kept.` | the installed program has a file that is not part of CE 7.7, or one with different content — for example, because the vendor's address now delivers another version | place an installation yourself; Anvil accepts any complete version there |
| `Getting CE failed — nothing was kept. See the log for details.` | anything technical in between — the log line below names it | see the table below |

For placing an installation yourself, see [Placing an installation
yourself](07-ce-bridge.md#placing-an-installation-yourself).

**Confirm in the log:** select the category `ce_bridge`. A failed download logs
`fetch failed ({Status}): {Name}: {Message}`; every other failure logs
`getting CE failed: {Reason}`, and the `{Reason}` says which case it was:

| `{Reason}` begins with | What it means | What to do |
|---|---|---|
| `no Proton with Wine found` | no installed Proton has the Wine that runs the installer | install a Proton version through Steam, press `Rescan`, then try again |
| `installer left no program folder` | the installer closed without installing the program | as in the first table |
| `CE check: unexpected files` | followed by each file the check turned down | as in the first table |
| `caged installer ended with` | the sealed-off run could not be prepared or finished — `bubblewrap` or Proton's Wine was missing or did not start, or the result could not be copied out | worth reporting, together with that line |
| `ce_bridge/7.7 is already taken; nothing was overwritten` | a folder named `7.7` is already in `ce_bridge/` but is not a complete installation | move or delete that folder, then try again |

A line `getting CE failed: {Type}: {Message}` instead names an error Anvil did not expect, such as
a file system error while placing the result.

## CE opens without the game

You attached CE with `Open this game in CE` ticked, but Cheat Engine came up as if you had
started it by hand: no process opened, and the game's name is not in its title bar.

**Opening without a process is the fallback, not a failure**, so the interface says nothing about
it. The log says why: select the category `ce_bridge` and look at the time you attached.

| Line in the log | What it means | What to do |
|---|---|---|
| `CE opens without a process: "Open this game in CE" is off for {AppId}` | the checkbox is not **saved** on for this game — ticked but not saved counts as off | tick it and press `Save` |
| `CE opens without a process: none found for {AppId}` | Anvil found no process of the game to open | see below |

For `none found`, the category `Sessions` usually has a line beginning with `no game pid` at the
same time, which names why — for example, that the game's container never accepted a program. Detach
CE and attach it again once the game is fully up. Or pick the game yourself in Cheat Engine's
process list: Cheat Engine runs in the game's environment, so the game is listed there.

When it works, the line is `CE opens the game with process id {ProcessId}` instead.

## The game closed when CE was detached

You pressed the cyan stop button on the CE Bridge card, and the game closed together with
Cheat Engine.

**Cause:** detaching asks Cheat Engine to close and gives it 15 seconds. If you changed a cheat
table, Cheat Engine first asks "Save changes?" and waits for your answer. Once the 15 seconds are
up, Anvil ends Cheat Engine by force — and if Cheat Engine's debugger is attached to the game at
that moment, ending Cheat Engine by force takes the game down with it. Anvil cannot see the
question, so it does not wait longer for it.

**What to do:** answer the question within the 15 seconds. Better still, save or close your table
in Cheat Engine before you detach; then there is no question to wait for.

**Confirm in the log:** select the category `Sessions`. A forced end reads
`asked {Count} process(es) in appid {AppId} to close`, followed 15 seconds later by
`still running in appid {AppId} after {Grace} s, forcing {Count} process(es)`. If Cheat Engine
closed on request, the second line is `closed on request in appid {AppId} after {Seconds} s`
instead.
