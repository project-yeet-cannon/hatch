"""
One-shot Uptime Kuma provisioning: first-run account setup, the Home
Assistant notification provider, and attaching that notification to every
existing monitor (including ones AutoKuma creates from static-monitors/).

Safe to run on every deploy: `setup` is a no-op once an account exists,
the notification is created once and re-synced afterward, and the
notification-attachment pass is idempotent.

Two entry points, and the reason for the split is an ordering deadlock:

    provision.py                 the full pass above, run hourly by
                                 deploy/cluster/observability/config/provisioning/
                                 kuma-provision.yaml
    provision.py --setup-only    the account setup alone, run as an
                                 initContainer on AutoKuma by
                                 deploy/cluster/observability/controllers/autokuma.yaml

`setup` here is the only thing in the repository that creates Kuma's admin
account, and AutoKuma cannot authenticate until it has run. But the CronJob
above lives in the observability-config Kustomization, which dependsOn
observability-controllers with wait: true - and AutoKuma is *in* controllers.
So the full pass can never run until AutoKuma is healthy, and AutoKuma can
never become healthy until the full pass has run. --setup-only breaks that by
moving the one step AutoKuma actually needs into AutoKuma's own pod, where it
runs before the container that depends on it and needs none of the Home
Assistant configuration the rest of this script does.
"""

import os
import sys
import time

from uptime_kuma_api import NotificationType, UptimeKumaApi, UptimeKumaException

KUMA_URL = os.environ["KUMA_URL"]
KUMA_USERNAME = os.environ["KUMA_USERNAME"]
KUMA_PASSWORD = os.environ["KUMA_PASSWORD"]

NOTIFICATION_NAME = "Home Assistant"


# Read when the notification pass actually needs them rather than at import,
# so --setup-only does not require Home Assistant configuration it never uses.
# The initContainer that runs it has no HA_TOKEN, and should not need one.
def ha_settings() -> dict:
    # One HA_URL rather than the HA_HOST/HA_PORT pair this used to split and
    # immediately rejoin below. The caller composes it, and has to: an env
    # `value` must be a string, but kustomize drops the quotes around
    # `"${HA_PORT}"` and Flux substitutes textually afterwards, so a bare port
    # number arrives at the API server as an integer and takes the entire
    # observability-config Kustomization down with it. See the header of
    # deploy/cluster/observability/config/provisioning/kuma-provision.yaml.
    return dict(
        url=os.environ["HA_URL"],
        token=os.environ["HA_TOKEN"],
        notify_service=os.environ.get("HA_NOTIFY_SERVICE", ""),
    )


# Tunable because the two callers wait on different things: the CronJob runs
# against a Kuma that has been up for a while, while the initContainer races
# its own pod's Uptime Kuma through a Longhorn attach and SQLite's first-run
# migration. Overshooting here is cheaper than an Init:CrashLoopBackOff that
# is really just a slow volume.
CONNECT_RETRIES = int(os.environ.get("KUMA_CONNECT_RETRIES", "15"))
CONNECT_RETRY_DELAY_SECONDS = int(os.environ.get("KUMA_CONNECT_RETRY_DELAY_SECONDS", "2"))


def connect() -> UptimeKumaApi:
    last_error = None
    for attempt in range(1, CONNECT_RETRIES + 1):
        try:
            return UptimeKumaApi(KUMA_URL)
        except Exception as e:  # noqa: BLE001 - retry on any connect failure
            last_error = e
            print(f"[{attempt}/{CONNECT_RETRIES}] waiting for Kuma at {KUMA_URL}: {e}")
            time.sleep(CONNECT_RETRY_DELAY_SECONDS)
    raise SystemExit(f"could not connect to Kuma: {last_error}")


def ensure_account(api: UptimeKumaApi) -> None:
    # Two things this gets wrong if written the obvious way, both found
    # against the pinned Uptime Kuma 2.4.0 rather than in review - and
    # together they are why AutoKuma's initContainer crash-looped, which
    # stalled observability-controllers, which left observability-config
    # (and with it the whole opensearch-provision CronJob) never applied.
    #
    #   1. The arguments have to be a *tuple*, not a list. python-socketio
    #      packs a tuple into multiple socket.io arguments and anything else
    #      into exactly one, so `[username, password]` arrives at Kuma's
    #      `socket.on("setup", (username, password, callback))` as a single
    #      array in `username`, with the ack function landing in `password`
    #      and `callback` left undefined. Kuma's handler then throws on the
    #      array, and its own `catch` dies calling that undefined callback
    #      (server.js:712 in 2.4.0) - so no ack is ever sent, and this call
    #      hangs until socketio's own timeout raises TimeoutError. Every
    #      multi-argument caller inside uptime_kuma_api itself passes a
    #      tuple; this was the one place in the repository that didn't.
    #   2. `needSetup` is the question to ask, not `setup`. The setup handler
    #      is only useful while Kuma has no account, and reading its failure
    #      to find that out is what the old `except UptimeKumaException`
    #      here was doing - which swallowed (1) into a silent "already
    #      configured" on any Kuma that answered at all, and would have gone
    #      on swallowing it.
    if not api._call("needSetup"):
        print("Kuma already has an admin account, nothing to set up")
        return

    try:
        api._call("setup", (KUMA_USERNAME, KUMA_PASSWORD))
        print("created initial Kuma admin account")
    except UptimeKumaException as e:
        # Only the setup race is survivable: the initContainer and the hourly
        # CronJob can both see needSetup true and both call setup. Anything
        # else - notably `passwordTooWeak`, which is what Kuma 2.x answers to
        # a KUMA_PASSWORD its check-password-strength gate rates "Too weak" -
        # has to fail the container rather than fall through to a login that
        # can only report it as authIncorrectCreds.
        if "has been initialized" not in str(e):
            raise
        print(f"another provisioner won the setup race: {e}")


def ensure_notification(api: UptimeKumaApi) -> int:
    ha = ha_settings()
    existing = next(
        (n for n in api.get_notifications() if n["name"] == NOTIFICATION_NAME), None
    )
    fields = dict(
        name=NOTIFICATION_NAME,
        isDefault=True,
        applyExisting=True,
        type=NotificationType.HOMEASSISTANT,
        notificationService=ha["notify_service"],
        homeAssistantUrl=ha["url"],
        longLivedAccessToken=ha["token"],
    )
    if existing is None:
        result = api.add_notification(**fields)
        print(f"created '{NOTIFICATION_NAME}' notification (id={result['id']})")
        return result["id"]

    api.edit_notification(existing["id"], **fields)
    print(f"updated '{NOTIFICATION_NAME}' notification (id={existing['id']})")
    return existing["id"]


def attach_notification_to_all_monitors(api: UptimeKumaApi, notification_id: int) -> None:
    for monitor in api.get_monitors():
        if notification_id in monitor["notificationIDList"]:
            continue
        api.edit_monitor(
            monitor["id"],
            notificationIDList=monitor["notificationIDList"] + [notification_id],
        )
        print(f"attached '{NOTIFICATION_NAME}' notification to monitor '{monitor['name']}'")


def main() -> None:
    setup_only = "--setup-only" in sys.argv[1:]
    api = connect()
    try:
        ensure_account(api)
        # The login is the gate, not the setup call: ensure_account swallows
        # "already configured", so an account that exists with a *different*
        # password would otherwise pass silently and leave AutoKuma failing
        # to authenticate behind a green initContainer - the exact symptom
        # deploy/cluster/observability/controllers/admin-secrets.yaml warns
        # about after a password is rotated in Kuma's UI alone. Letting this
        # raise fails the container instead.
        api.login(KUMA_USERNAME, KUMA_PASSWORD)
        if setup_only:
            print(f"Kuma admin account is usable by '{KUMA_USERNAME}'")
            return
        notification_id = ensure_notification(api)
        attach_notification_to_all_monitors(api, notification_id)
    finally:
        api.disconnect()


if __name__ == "__main__":
    main()
