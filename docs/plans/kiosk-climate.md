# Kiosk Climate

New Kiosk Dashboard UI for house climate controls.

The Routines implementation has been great, I have loved it. I want to add a next-tier interaction. I don't know what to call it, so I will just call it a `Submenu` for now. This is explicitly a placeholder and I want you to suggest new names. `Submenu`s appear between `Routine`s and `Gather`s in the dashboard UI and appear as similar UI squares. They have the same UI customization as `Routine`s - name, icon, color.

When tapping on a `Submenu`, a modal pops up which has multiple calls to action. Some of these are `Routine`s. Some are a new type of element, which I will placeholder name a `Control` (again this is an explicit placeholder, place recommend a more precise domain name).

A `Control` is a more device-specific element which performs richer controls than a simple tap. Architecturally, it may make sense to implement a `Control` as a composition of `Routine`s, or maybe there is just some conceptual overlap - I do not know and I am open to whatever is best. Ask me any clarifying questions about vision or future direction that will help hash it out.

Example user experience for this epic:

- As a kiosk tablet user, I scroll down the page past Routines. I see a box named "Climate."
- When I tap the "Climate" box, a sub-UI (probably a modal, but I don't want to be prescriptive) pops up with more actions that I can take, some of which are rich inputs:
    - Air Conditioner control: minimal cluster of buttons to control an AC: cool/off modes, +/- degree, set temperature interaction (either digit input or a scroll wheel)
    - Fan 1 on/off
    - Fan 2 on/off
    - Living room radiator thermostate control: almost the same as AC controls, but heat/off are modes instead of cool/off. A single "on/off" user language could be nice.
- There is another box, environment
    - For several smart lights, a simplified smart light control widget:
        - Name
        - Color picker
        - Brightness
        - On/Off
- For cameras with controls, a `Submenu` could include a `Control` which is a live stream of a web camera with whatever controls are available (e.g. left/right/up/down, zoom, light on/off, color on/ff)

Scope:
- Determine good domain terms for `Submenu` and `Control`
- Implement API, admin UI, and kiosk dashboard UI code for those elements
- As MVP, we implement the `Climate` `Submenu`:
    - AC `Control` with the controls:
        - on/off
        - +/- 1 degree F
        - set temperature
    - Fan 1 on/off
    - Fan 2 on/ff

All other `Submenu` or `Control`
