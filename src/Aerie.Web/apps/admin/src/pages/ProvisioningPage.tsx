import { useEffect, useRef, useState } from 'react';
import { Button, Card, Field, PageHeader, Text } from '@aerie/ui';
import QRCode from 'qrcode';
import { getKioskProvisioningInfo } from '../api/client';
import type { ProvisioningInfo } from '../types';

/** Builds the Android QR provisioning payload from a ProvisioningInfo - see docs/git history for the extras list this mirrors. */
function buildProvisioningPayload(info: ProvisioningInfo): Record<string, string | boolean> {
  const payload: Record<string, string | boolean> = {
    'android.app.extra.PROVISIONING_DEVICE_ADMIN_COMPONENT_NAME': info.deviceAdminComponentName,
    'android.app.extra.PROVISIONING_DEVICE_ADMIN_PACKAGE_DOWNLOAD_LOCATION': info.apkDownloadUrl,
    'android.app.extra.PROVISIONING_DEVICE_ADMIN_SIGNATURE_CHECKSUM': info.signatureChecksum,
    'android.app.extra.PROVISIONING_WIFI_SSID': info.wifiSsid,
    'android.app.extra.PROVISIONING_LOCALE': 'en_US',
    'android.app.extra.PROVISIONING_TIME_ZONE': info.timeZone,
    'android.app.extra.PROVISIONING_SKIP_ENCRYPTION': true,
    'android.app.extra.PROVISIONING_LEAVE_ALL_SYSTEM_APPS_ENABLED': true,
  };
  // An empty security type means an open network - the key must be omitted
  // entirely rather than sent blank, per Android's provisioning contract.
  if (info.wifiSecurityType) {
    payload['android.app.extra.PROVISIONING_WIFI_SECURITY_TYPE'] = info.wifiSecurityType;
    payload['android.app.extra.PROVISIONING_WIFI_PASSWORD'] = info.wifiPassword;
  }
  return payload;
}

export function ProvisioningPage() {
  const [info, setInfo] = useState<ProvisioningInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    load();
  }, []);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      setInfo(await getKioskProvisioningInfo());
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  const hasWifi = !!info?.wifiSsid;

  useEffect(() => {
    if (!info || !hasWifi || !canvasRef.current) return;
    QRCode.toCanvas(canvasRef.current, JSON.stringify(buildProvisioningPayload(info)), { width: 320 }).catch((err) =>
      setError(err instanceof Error ? err.message : String(err)),
    );
  }, [info, hasWifi]);

  async function copyJson() {
    if (!info) return;
    await navigator.clipboard.writeText(JSON.stringify(buildProvisioningPayload(info), null, 2));
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  }

  return (
    <div>
      <PageHeader
        title="Provisioning"
        actions={<Button onClick={load}>Refresh</Button>}
      />

      {error && <Text tone="danger" className="mb-2">{error}</Text>}
      {loading && <Text tone="muted">Loading…</Text>}

      {!loading && info && !hasWifi && (
        <Card>
          <p>
            No kiosk Wi-Fi network is configured yet. Set <strong>Kiosk Wi-Fi SSID</strong> (and password/security
            type, if needed) on the Settings page before generating a provisioning QR code.
          </p>
        </Card>
      )}

      {!loading && info && hasWifi && (
        <Card>
          <Field as="div" label="Encoding" className="mb-2">
            <Text tone="muted">
              SSID <strong>{info.wifiSsid}</strong> ({info.wifiSecurityType || 'open'}) · APK from{' '}
              <strong>{info.apkDownloadUrl}</strong> · time zone <strong>{info.timeZone || '—'}</strong>
            </Text>
          </Field>

          <canvas ref={canvasRef} />

          <div className="mt-2">
            <Button onClick={copyJson}>
              {copied ? 'Copied!' : 'Copy JSON'}
            </Button>
          </div>
        </Card>
      )}
    </div>
  );
}
