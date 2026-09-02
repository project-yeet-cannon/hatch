import { Button, Card, EmptyState, Table, Text } from '@aerie/ui';
import { GalleryPage, GallerySection } from '../components/Gallery';

export function EmptyStatePage() {
  return (
    <GalleryPage
      title="Empty state"
      blurb="The exception the renders-nothing rule needs. A component with nothing to say renders nothing — but sometimes the absence is the news, and a page that stays blank reads as a page that failed to load."
    >
      <GallerySection
        title="What it renders"
        note="Admin's existing muted sentence, and nothing more. No illustration, no headline, no bordered box: what an empty state should look like is a design decision, and this phase's job was to make it one decision instead of forty scattered muted paragraphs."
      >
        <Card>
          <EmptyState message="No zones yet." />
        </Card>
      </GallerySection>

      <GallerySection
        title="With the one thing to do about it"
        note="When there is an obvious next action, it sits on the same line. When there is not, there is no button — an empty state with a disabled call to action is a dead end with a decoration on it."
      >
        <Card>
          <EmptyState message="No albums yet." action={<Button variant="primary">Create an album</Button>} />
        </Card>
      </GallerySection>

      <GallerySection
        title="Instead of an empty table"
        note="This is the pairing that matters. A header row above no rows is a component insisting it has something to say; the table renders nothing and the empty state speaks instead."
      >
        <div className="compare">
          <div className="compare-half">
            <Text tone="muted" as="span">
              What it replaces
            </Text>
            <Card flush>
              <Table>
                <thead>
                  <tr>
                    <th>Name</th>
                    <th>Kind</th>
                    <th>Included</th>
                  </tr>
                </thead>
                <tbody>
                  <tr>
                    <Text as="td" tone="muted" colSpan={3}>
                      No rows
                    </Text>
                  </tr>
                </tbody>
              </Table>
            </Card>
          </div>
          <div className="compare-half">
            <Text tone="muted" as="span">
              What it renders
            </Text>
            <Card>
              <EmptyState message="No zones yet." action={<Button variant="primary">Add zone</Button>} />
            </Card>
          </div>
        </div>
      </GallerySection>

      <GallerySection
        title="Not for errors, and not for loading"
        note='"Nothing here" and "something broke" are different facts, and a page that says the first when it means the second sends the operator looking in the wrong place. Those two stay a danger-toned <Text> and a muted one.'
      >
        <div className="stack">
          <Card>
            <EmptyState message="No sessions." />
          </Card>
          <Card>
            <Text tone="muted">Loading…</Text>
          </Card>
          <Card>
            <Text tone="danger">The session list could not be read: the API returned 503.</Text>
          </Card>
        </div>
      </GallerySection>

      <GallerySection
        title="Long content"
        note="A sentence in the words of the thing that is missing — 'No zones yet', not 'No data'. If it needs two sentences, the second one belongs in the page, not in the empty state."
      >
        <Card>
          <EmptyState message="No devices have been discovered on this subnet yet. Discovery runs every five minutes." />
        </Card>
      </GallerySection>
    </GalleryPage>
  );
}
