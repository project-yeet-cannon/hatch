"""
One-shot Uptime Kuma provisioning: first-run account setup, the Home
Assistant notification provider, and attaching that notification to every
existing monitor (including ones AutoKuma creates from static-monitors/).

Safe to run on every deploy: `setup` is a no-op once an account exists,
the notification is created once and re-synced afterward, and the
notification-attachment pass is idempotent.
"""

import os
import sys
import time

from uptime_kuma_api import NotificationType, UptimeKumaApi, UptimeKumaException

KUMA_URL = os.environ["KUMA_URL"]
KUMA_USERNAME = os.environ["KUMA_USERNAME"]
KUMA_PASSWORD = os.environ["KUMA_PASSWORD"]

HA_HOST = os.environ["HA_HOST"]
HA_PORT = os.environ["HA_PORT"]
HA_TOKEN = os.environ["HA_TOKEN"]
HA_NOTIFY_SERVICE = os.environ.get("HA_NOTIFY_SERVICE", "")

NOTIFICATION_NAME = "Home Assistant"

CONNECT_RETRIES = 15
CONNECT_RETRY_DELAY_SECONDS = 2


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
    try:
        api._call("setup", [KUMA_USERNAME, KUMA_PASSWORD])
        print("created initial Kuma admin account")
    except UptimeKumaException as e:
        print(f"skipping setup (already configured): {e}")


def ensure_notification(api: UptimeKumaApi) -> int:
    existing = next(
        (n for n in api.get_notifications() if n["name"] == NOTIFICATION_NAME), None
    )
    fields = dict(
        name=NOTIFICATION_NAME,
        isDefault=True,
        applyExisting=True,
        type=NotificationType.HOMEASSISTANT,
        notificationService=HA_NOTIFY_SERVICE,
        homeAssistantUrl=f"http://{HA_HOST}:{HA_PORT}",
        longLivedAccessToken=HA_TOKEN,
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
    api = connect()
    try:
        ensure_account(api)
        api.login(KUMA_USERNAME, KUMA_PASSWORD)
        notification_id = ensure_notification(api)
        attach_notification_to_all_monitors(api, notification_id)
    finally:
        api.disconnect()


if __name__ == "__main__":
    main()
