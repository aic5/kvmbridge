# Stream Deck buttons

Install the Mac apps and client configuration first. In the Stream Deck editor:

1. Drag **System → Open** onto an empty key.
2. Set **App / File** to the local `KVM Computer 1.app` path.
3. Set the title to the computer's name or `Computer 1`.
4. Choose `streamdeck/icons/computer-1.png` as its custom icon.
5. Repeat for computers 2–4.

Use the `.app` files for silent operation. Opening a `.command` file uses Terminal.
No Stream Deck plugin is required. The icons are original project assets covered by
the MIT license; their numbers correspond to physical KVM inputs. PNG and SVG variants
are included. A contact sheet is at `streamdeck/icons/preview.png`.

| Input | Launcher | Icon |
| --- | --- | --- |
| 1 | KVM Computer 1.app | computer-1.png |
| 2 | KVM Computer 2.app | computer-2.png |
| 3 | KVM Computer 3.app | computer-3.png |
| 4 | KVM Computer 4.app | computer-4.png |

## Pages and folders

A folder named Apps is an ordinary folder. Its buttons have the same abilities as those
on a profile's main page. Double-click its upper-left back arrow in the editor to leave
it. At the profile's top level, the **+** below the grid creates another page. Leave an
empty key for page navigation. Select a button and use **Cmd+C**, go to the destination
page, select an empty key, and use **Cmd+V** to copy it.

## Export your configured profile

Open Stream Deck **Settings/Preferences → Profiles**, right-click your profile, and
choose **Export**. Double-click the exported `.streamDeckProfile` file to import it later.
The export stores the button configuration, not the external launcher apps or credentials.
Paths and credentials must be set up on each destination Mac. This repository supplies
portable button assets and instructions; it does not distribute a personal Stream Deck profile.

Official references:
- [System actions](https://help.elgato.com/hc/en-us/articles/360028234471)
- [Pages](https://help.elgato.com/hc/en-us/articles/4410312027277)
- [Profile export](https://docs.elgato.com/streamdeck/sdk/guides/profiles/)
