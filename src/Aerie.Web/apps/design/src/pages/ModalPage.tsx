import { useState } from 'react';
import { Button, Field, Grid, Modal, Text } from '@aerie/ui';
import { GalleryPage, GallerySection } from '../components/Gallery';

/* Every modal here is the real one: it takes over the page, escape closes it,
   the scrim closes it, and focus goes in and comes back. A specimen drawn
   inline in the page could show the paint and none of that, and the behaviour
   is the part worth checking. */

type Which = 'short' | 'form' | 'long' | 'pinned' | 'title' | null;

export function ModalPage() {
  const [open, setOpen] = useState<Which>(null);
  const close = () => setOpen(null);

  return (
    <GalleryPage
      title="Modal"
      blurb="The dialog. Admin had this as its own component and it moved here whole — plus the three things it was missing: a dialog role, focus that goes in and comes back, and an escape key that works from inside an input."
    >
      <GallerySection
        title="Open one"
        note="Then press Escape, or click the scrim, or tab around inside. When it closes, focus returns to the button that opened it — before this, a keyboard user pressed Tab after closing and walked the page from the top."
      >
        <div className="row">
          <Button variant="primary" onClick={() => setOpen('short')}>
            A confirmation
          </Button>
          <Button onClick={() => setOpen('form')}>A form</Button>
          <Button onClick={() => setOpen('long')}>Long content</Button>
          <Button onClick={() => setOpen('title')}>A long title</Button>
        </div>
      </GallerySection>

      <GallerySection
        title="Actions that cannot scroll away"
        note="Given a footer, the panel stops scrolling and the body scrolls inside it. The two dozen lines below move; the two buttons do not, at any window height. Without a footer the panel is the scroller it has always been, which is what the four other specimens here still are."
      >
        <div className="row">
          <Button onClick={() => setOpen('pinned')}>Long content, pinned actions</Button>
        </div>
      </GallerySection>

      <GallerySection
        title="What it is not"
        note="Not a full focus trap. That wants either <dialog>'s top-layer behaviour or a well-tested library, and choosing between those is a bigger decision than the primitives phase gets to make. Focus starts inside and returns; Tab can still leave. It is written down in the plan rather than left to be discovered."
      >
        <Text tone="muted">
          Nothing to see here — this section is the note.
        </Text>
      </GallerySection>

      <Modal open={open === 'short'} onClose={close} title="Delete this album?">
        <Text tone="muted">Its 214 photos stay on disk. Only the album goes.</Text>
        <div className="row row--top">
          <Button variant="danger" onClick={close}>
            Delete album
          </Button>
          <Button onClick={close}>Keep it</Button>
        </div>
      </Modal>

      <Modal open={open === 'form'} onClose={close} title="Add a calendar">
        <Grid cols={2}>
          <Field label="Display name">
            <input type="text" placeholder="Household" />
          </Field>
          <Field label="Feed URL" hint="An .ics URL the server can reach.">
            <input type="text" placeholder="https://calendar.example/feed.ics" />
          </Field>
        </Grid>
        <div className="row row--top">
          <Button variant="primary" onClick={close}>
            Add calendar
          </Button>
          <Button onClick={close}>Cancel</Button>
        </div>
        <Text tone="muted" className="modal-note">
          Escape closes this from inside the input too — the key is bound to the document, not to the window.
        </Text>
      </Modal>

      <Modal open={open === 'long'} onClose={close} title="Discovery log">
        <div className="stack">
          {Array.from({ length: 24 }, (_, index) => (
            <Text key={index} tone="muted">
              192.0.2.{index + 10} answered on mDNS and matched no adapter in the catalogue.
            </Text>
          ))}
        </div>
        <div className="row row--top">
          <Button onClick={close}>Close</Button>
        </div>
        <Text tone="muted" className="modal-note">
          The panel scrolls; the page behind it does not move. The panel caps at the viewport height less one
          spacing rung, so the scrim is visible above and below it and there is somewhere to click out.
        </Text>
      </Modal>

      <Modal
        open={open === 'pinned'}
        onClose={close}
        title="Discovery log"
        footer={
          <div className="row">
            <Button variant="primary" onClick={close}>
              Adopt all 24
            </Button>
            <Button onClick={close}>Close</Button>
          </div>
        }
      >
        <div className="stack">
          {Array.from({ length: 24 }, (_, index) => (
            <Text key={index} tone="muted">
              192.0.2.{index + 10} answered on mDNS and matched no adapter in the catalogue.
            </Text>
          ))}
        </div>
      </Modal>

      <Modal
        open={open === 'title'}
        onClose={close}
        title="Regenerate the provisioning token for every unpaired panel"
      >
        <Text tone="muted">
          A long title wraps and pushes the close control down with it. The close stays where it started, at
          the top right, because a control that moves to the middle of a heading is a control nobody finds
          twice.
        </Text>
        <div className="row row--top">
          <Button onClick={close}>Close</Button>
        </div>
      </Modal>
    </GalleryPage>
  );
}
