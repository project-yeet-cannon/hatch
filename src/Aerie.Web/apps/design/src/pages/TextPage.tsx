import { Card, Table, Text } from '@aerie/ui';
import { GalleryPage, GallerySection } from '../components/Gallery';

export function TextPage() {
  return (
    <GalleryPage
      title="Text"
      blurb="Toned text — 126 sites in admin, and the most-used idiom in the house. It sets color and nothing else."
    >
      <GallerySection
        title="The four tones"
        note="A tone says what a phrase means, not how loud it is. Default inherits the ink it sits in, which is what a <Text> inside a table cell wants."
      >
        <div className="stack">
          <Text>The default. No class at all — it inherits --ink from whatever it sits in.</Text>
          <Text tone="muted">Muted. Secondary information, a timestamp, the sentence under a heading.</Text>
          <Text tone="danger">Danger. Something failed, and the operator has to know.</Text>
          <Text tone="success">Success. Something worked, and saying so is worth a line.</Text>
        </div>
      </GallerySection>

      <GallerySection
        title="Any element"
        note="The tone goes on the element the content already wanted to be. Wrapping a cell's contents in a span to color them would put an inline box inside every cell with an opinion."
      >
        <Card flush>
          <Table>
            <thead>
              <tr>
                <th>Device</th>
                <th>Last seen</th>
                <th>State</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>Living room sensor</td>
                <Text as="td" tone="muted">
                  2 minutes ago
                </Text>
                <Text as="td" tone="success">
                  Reporting
                </Text>
              </tr>
              <tr>
                <td>Garage door</td>
                <Text as="td" tone="muted">
                  9 days ago
                </Text>
                <Text as="td" tone="danger">
                  Unreachable
                </Text>
              </tr>
            </tbody>
          </Table>
        </Card>
      </GallerySection>

      <GallerySection
        title="Inside a sentence"
        note='as="span" for a phrase in running text. The tone is a property of the phrase, and the paragraph around it keeps its own ink.'
      >
        <p>
          The kitchen panel reported <Text as="span" tone="success">healthy</Text> at 14:02 and has been{' '}
          <Text as="span" tone="danger">unreachable</Text> since. Its last known address was{' '}
          <Text as="span" tone="muted">192.0.2.44</Text>.
        </p>
      </GallerySection>

      <GallerySection
        title="Long content"
        note="It sets no measure and no margin. A paragraph is as wide as the column it is in, and the page decides how far apart its paragraphs sit — a Text that owned its own spacing would be fought at every site that wanted the tone without it."
      >
        <div className="stack measure-cap">
          <Text tone="muted">
            Discovery scans the local subnet for devices announcing themselves over mDNS and SSDP, matches
            each against the adapter catalogue, and lists the ones it can identify. A device that answers on
            neither protocol has to be added by address, which is what the form below the list is for.
          </Text>
          <Text tone="danger">
            The scan finished with three addresses that answered but could not be identified. They are listed
            under Unmatched with the raw records they returned, in case the adapter for them is one this
            install does not have yet.
          </Text>
        </div>
      </GallerySection>
    </GalleryPage>
  );
}
