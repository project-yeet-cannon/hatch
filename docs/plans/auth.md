we need some auth.

i want it to be easy, self-owned, and not based on any cloud provider.

i want it to be stupid simple for my family to use, and have an option for an auth flow that doesn't involve a username password. i want to optimize for long-term ease of use for my family; i am willing to incur administrative overhead to accomplish this.

i want to auth-wall all of our apps behind this auth provider. if we can ultimately weave in our observability tier to our auth provider, even better; not a big concern right now though.

some auth options i've thought of:

- wait-for-approval / permanent grant: simplest flow, most of the time i want to grant permanent authentication to a particular device. user flow: my partner gets a new phones, goes to home.landis.family, gets an auth wall, i go to my admin auth portal and grant her device's request
- scan QR code - give me the ability in the admin app to generate a qr code that our identity provider can use to permanently authenticate someone
- do you have other/better simple long-term auth ideas? these are effectively ways of distributing an api key, i suppose

for the most part i want my family to have 'magic' auth that just works for them after an initial setup. I want to have an admin control panel of permanent sessions/tokens, and ultimately i will want to tie them to users/roles/scopes for fine grained authorization control.

What I want from you:
- any better ideas for auth schemes
- an implementation plan which accomplishes the following goals with the lowest lift and fastest shipping speed:
  - apps that we have written are behind an auth provider
  - api calls are authentication-gated (just a simple auth gate so far, no need to create fine grained permissions)
  - in the admin app, i can either create or approve new sessions
  - in the admin app, i can view and delete existing sessions
