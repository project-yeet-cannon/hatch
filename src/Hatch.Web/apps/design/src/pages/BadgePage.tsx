import { Badge, Card, Table, Text } from '@hatch/ui';
import { GalleryPage, GallerySection } from '../components/Gallery';

export function BadgePage() {
  return (
    <GalleryPage
      title="Badge"
      blurb="A pill stating one fact about the thing next to it: enabled, online, included. Most of admin's are a boolean rendered as Yes/No."
    >
      <GallerySection
        title="The four tones"
        note="Muted is the resting state — a fact about a row, not a warning about it. The three accents are for a badge with something to say. Each is an accent wash under the matching ink, which is the pairing to check when the ramp moves."
      >
        <div className="row">
          <Badge>Disabled</Badge>
          <Badge tone="success">Online</Badge>
          <Badge tone="danger">Unreachable</Badge>
          <Badge tone="primary">Pending</Badge>
        </div>
      </GallerySection>

      <GallerySection
        title="Where they actually live"
        note="In a table cell, one per row, in a column of them. That column is the context to judge the tones in: eight muted pills with one green in the middle is the shape an operator scans."
      >
        <Card flush>
          <Table>
            <thead>
              <tr>
                <th>Panel</th>
                <th>Paired</th>
                <th>State</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>Kitchen</td>
                <td>
                  <Badge tone="success">Yes</Badge>
                </td>
                <Text as="td" tone="muted">
                  Awake
                </Text>
              </tr>
              <tr>
                <td>Hallway</td>
                <td>
                  <Badge>No</Badge>
                </td>
                <Text as="td" tone="muted">
                  Never seen
                </Text>
              </tr>
              <tr>
                <td>Workshop</td>
                <td>
                  <Badge tone="success">Yes</Badge>
                </td>
                <Text as="td" tone="muted">
                  Asleep
                </Text>
              </tr>
            </tbody>
          </Table>
        </Card>
      </GallerySection>

      <GallerySection
        title="Long content"
        note="A badge does not wrap and does not truncate: it is one word about one fact. A badge that needs a sentence is a cell, and it should be one."
      >
        <div className="row">
          <Badge tone="danger">Certificate expired 41 days ago</Badge>
        </div>
      </GallerySection>

      <GallerySection
        title="In a sentence"
        note="It is inline-block, so it sits on the text baseline without pushing the line height around."
      >
        <p>
          The workshop panel is <Badge tone="success">Online</Badge> and the garage panel is{' '}
          <Badge tone="danger">Unreachable</Badge>, which is why the porch routine did not fire.
        </p>
      </GallerySection>
    </GalleryPage>
  );
}
