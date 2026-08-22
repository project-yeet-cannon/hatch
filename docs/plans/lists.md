i want to add a new feature to the Aerie ecosystem called `Lists`. in general, `Lists` manages shopping lists shared by the family.

the primary use case is tracking grocery lists, but we want to provide lists for various categories of stores so that this is more broadly applicable.

please come up with a few fun/pithy alternative names for `Lists`, i'm not sold on the name, it's just an on-the-nose placeholder. i like fun product-y names.

interaction spreads across devices, described in user stories:

### 1. Kiosk entry
as a family member in the kitchen, when i notice that an item is getting low, i want to be able to interact with the wall-mounted kiosk tablet to add it to a shopping list.

implementation-wise, we will add functionality to the dashboard web app. it will start dumb - CRUD navigation of lists - but ultimately we'll want to make it easier to use, shortcuts/speed dials and maybe voice activation.

### 2. Family PWA
as a family member using a phone running the `family` pwa, i can open a `Lists` Household app which provides me CRUD access to shopping lists with checkbox inputs. there is a function to clear all checked entries.

the lists shall be persisted in the aerie api such that kiosk and pwa entries are using the same source of truth.

for the time being, we will depend upon family members running TailScale when outside the house for access to the server.

