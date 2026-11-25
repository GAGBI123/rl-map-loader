# airdribble-patch

Python scripts used to fix the *Air Dribble Challenge* workshop map in place (every object keeps its exact size
and offset). `upk.py` reads uncompressed UDK (v868) packages, `kgraph.py` prints the Kismet graph,
`ptree.py` is a lossless tagged-property editor, and `patch_airdribble.py` applies the fix:

- sets `MaxTriggerCount = 0` on the *Car Spawned* event and the *Reset* remote events. Kismet events default to
  firing once, so the map's own reset logic stopped after the first reset.
- when the car spawns, also runs the map's existing Teleport action (players → the map's start), in the same
  moment. Freeplay resets were spawning the car somewhere else every other time; now every reset lands at the start
  with no visible second reset.
- links the final-time / best-time texts to the player like the live timer

```powershell
python patch_airdribble.py AirDribble_Challenge.upk AirDribble_Challenge_Fixed.upk
python kgraph.py AirDribble_Challenge_Fixed.upk
```
