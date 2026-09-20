# What the scenarios in this suite are worth

Every scenario here plays the same script against both clients and requires
the two transcripts to match. That is not the same thing as evidence, and the
difference matters when reading a green run.

**Parity evidence.** The two clients answer from different code, so a
scenario over one of these could genuinely come out differently and a match
says something:

- the session bindings each client builds and what happens on connect, on
  taking hold of a character and on arriving in the world;
- the character bindings: the skill formulas, the confirmation hooks and how
  fast the character runs;
- the plugin events each client raises, and the surfaces a plugin reaches
  for -- hotkeys, storage, world lines, the clipboard, the host window;
- how often a plugin is ticked and how much elapsed time each tick carries.
  This is the one scenario that drives the two arms differently on purpose --
  short drawn frames against scheduler turns -- because that difference is
  exactly what it is there to rule out;
- each client's own outbound command route and its own per-frame driver:
  movement, attacks, chat sends, what actually goes out on the wire;
- what each client passes the shared plugin surface and the shared runtime,
  which is what the census reads.

**Regression guards.** The owner these exercise has already moved into the
runtime, so both arms run the same code and agreement is true by
construction. They are still worth keeping -- they fail if the owner is
pulled back out into one client, which has happened -- but a green run over
them is not evidence that two implementations agree:

- items and looting, selection cycling, dismissing a ghost, using a world
  object, walking to something and then using it, the chat entry box;
- the objects a plugin can see and where they are.

A scenario whose owner moves into the runtime moves from the first list to
the second; nothing about the scenario itself changes. Say which list a new
scenario belongs to in its own doc comment, and mutation-check it against
the client it is meant to speak for -- a scenario in the first list that
stays green when one client's half is taken away is in the second list
without knowing it.

## What the remote-body arms do not cover

The two arms that carry another creature's body run each client's own drive
over the same shared owners, which is the point of them. Two limits are worth
knowing before reading a green run over those scenarios:

- **Neither arm runs the client-with-a-window's inbound sink.** That sink
  cannot be built without a drawn world, so both arms deliver an accepted
  position the way the windowless sink does. The two production sinks are
  held to the same shape by a source pin instead, which compares the shared
  steps each runs, their order around the contact routing and the wire-cell
  adoption, and refuses an inlined copy of the bookkeeping.
- **Each arm now moves its own clock with the frame**, as both clients do,
  so a throttle measured against it can lapse rather than refusing forever.
  No scenario here yet turns on that clock: the two things that read it --
  the body's own physics host and the placement drive's arrival stamp -- are
  not reached by the way these arms stage a creature, so a scenario written
  against either of them proves nothing today. Reaching them is the next
  piece of work on this harness, and until it lands the clock is correct
  rather than demonstrated.
