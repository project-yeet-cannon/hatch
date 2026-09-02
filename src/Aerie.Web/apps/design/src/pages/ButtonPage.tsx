import { useState } from 'react';
import { Button, Card, Text } from '@aerie/ui';
import { GalleryPage, GallerySection } from '../components/Gallery';

/* Every button on this page is live. Press one and it does what it says —
   the disabled row toggles, the loading row runs a two-second fake save. A
   specimen you cannot press is a specimen that cannot show you its focus ring,
   its hover, or what it feels like to press a control that is already busy. */

export function ButtonPage() {
  const [saving, setSaving] = useState(false);
  const [disabled, setDisabled] = useState(true);

  function fakeSave() {
    setSaving(true);
    setTimeout(() => setSaving(false), 2000);
  }

  return (
    <GalleryPage
      title="Button"
      blurb="The pressable thing, in three weights. 110 sites in admin — and three quarters of them are secondary, which is why that is the default."
    >
      <GallerySection
        title="The three variants"
        note="One primary action per page. Danger is for the action that loses something. Everything else is secondary, including most of what a page does."
      >
        <div className="row">
          <Button variant="primary">Add zone</Button>
          <Button variant="secondary">Edit</Button>
          <Button variant="danger">Delete</Button>
        </div>
      </GallerySection>

      <GallerySection
        title="Disabled"
        note="Half opacity and a not-allowed cursor. The button stays in the layout and stays readable — a control that disappears when it cannot be used is a control the operator hunts for."
      >
        <div className="row">
          <Button variant="primary" disabled={disabled}>
            Save
          </Button>
          <Button variant="secondary" disabled={disabled}>
            Cancel
          </Button>
          <Button variant="danger" disabled={disabled}>
            Delete
          </Button>
          <Button onClick={() => setDisabled((value) => !value)}>
            {disabled ? 'Enable them' : 'Disable them'}
          </Button>
        </div>
      </GallerySection>

      <GallerySection
        title="Loading"
        note="Press Save. It stops taking presses for two seconds and reports aria-busy, and it looks exactly like disabled — which is deliberate: what a loading button should look like is a design decision, and the label does not change, so the row does not reflow while a request is in flight."
      >
        <div className="row">
          <Button variant="primary" loading={saving} onClick={fakeSave}>
            Save
          </Button>
          <Text tone="muted" as="span">
            {saving ? 'Saving…' : 'Idle'}
          </Text>
        </div>
      </GallerySection>

      <GallerySection
        title="Focus"
        note="Tab through these. The filled variants ring in --on-accent rather than --primary, because a primary ring on the primary fill is an invisible one."
      >
        <div className="row">
          <Button variant="primary">Primary</Button>
          <Button variant="secondary">Secondary</Button>
          <Button variant="danger">Danger</Button>
        </div>
      </GallerySection>

      <GallerySection
        title="Long labels, and a narrow column"
        note="A button is as wide as its label. It does not wrap and it does not truncate — a truncated action is an action nobody can read. If it does not fit, the label is too long."
      >
        <div className="stage stage--narrow">
          <div className="stage-page">
            <div className="row">
              <Button variant="primary">Regenerate provisioning token</Button>
              <Button>Cancel</Button>
            </div>
          </div>
        </div>
      </GallerySection>

      <GallerySection
        title="On a card"
        note="The ground buttons actually sit on in admin: --card, not --bg. Secondary is --line on --card, which is the pairing to check when the ramp moves."
      >
        <Card>
          <h3>Delete this album?</h3>
          <Text tone="muted">Its 214 photos stay on disk. Only the album goes.</Text>
          <div className="row row--top">
            <Button variant="danger">Delete album</Button>
            <Button>Keep it</Button>
          </div>
        </Card>
      </GallerySection>
    </GalleryPage>
  );
}
