import { Badge, Button, Card, EmptyState, Table, Text } from '@aerie/ui';
import { GalleryPage, GallerySection } from '../components/Gallery';

export function TablePage() {
  return (
    <GalleryPage
      title="Table"
      blurb="The rows. It owns the appearance and nothing else — <thead> and <tbody> pass straight through, which is what keeps admin's editing rows and nested tables plain JSX."
    >
      <GallerySection
        title="A table, in the card it lives in"
        note="Headers sit at the label register — uppercase, tracked out, muted — so the header row reads as a legend rather than as the first row of data. Rows are separated by a hairline, and the last one has none: a rule above nothing is a rule that looks like a missing row."
      >
        <Card flush>
          <Table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Kind</th>
                <th>Comfort range (°F)</th>
                <th>Included</th>
                <th />
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>Living Room</td>
                <td>Interior</td>
                <td>68 – 74</td>
                <td>
                  <Badge tone="success">Yes</Badge>
                </td>
                <td>
                  <div className="row row--tight">
                    <Button>Edit</Button>
                    <Button variant="danger">Delete</Button>
                  </div>
                </td>
              </tr>
              <tr>
                <td>Back Porch</td>
                <td>Outside</td>
                <Text as="td" tone="muted">
                  —
                </Text>
                <td>
                  <Badge>No</Badge>
                </td>
                <td>
                  <div className="row row--tight">
                    <Button>Edit</Button>
                    <Button variant="danger">Delete</Button>
                  </div>
                </td>
              </tr>
            </tbody>
          </Table>
        </Card>
      </GallerySection>

      <GallerySection
        title="An editing row"
        note="Admin's tables put a form in a colSpan row rather than opening a dialog to change one value. That is the reason this component is not column-driven: a columns={[…]} API would be a smaller call site for the simple tables and a wall for this one."
      >
        <Card flush>
          <Table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Kind</th>
                <th />
              </tr>
            </thead>
            <tbody>
              <tr>
                <td colSpan={3}>
                  <div className="row">
                    <input type="text" defaultValue="Living Room" />
                    <select defaultValue="Interior">
                      <option>Interior</option>
                      <option>Outside</option>
                    </select>
                    <Button variant="primary">Save</Button>
                    <Button>Cancel</Button>
                  </div>
                </td>
              </tr>
              <tr>
                <td>Back Porch</td>
                <td>Outside</td>
                <td>
                  <Button>Edit</Button>
                </td>
              </tr>
            </tbody>
          </Table>
        </Card>
      </GallerySection>

      <GallerySection
        title="Empty"
        note="No table at all. A header row above nothing is a component insisting it has something to say when it does not — the renders-nothing rule — and the empty state is what says so instead."
      >
        <Card>
          <EmptyState message="No zones yet." action={<Button variant="primary">Add zone</Button>} />
        </Card>
      </GallerySection>

      <GallerySection
        title="Too many columns"
        note="scroll turns the table into a sideways-scrolling box. It is off by default and opted into per table, because an overflow box is a clipping context and admin puts absolutely-positioned things — a combobox dropdown — inside table cells."
      >
        <Card flush>
          <Table scroll>
            <thead>
              <tr>
                <th>Session</th>
                <th>Person</th>
                <th>Device</th>
                <th>Address</th>
                <th>User agent</th>
                <th>Signed in</th>
                <th>Last seen</th>
                <th>Expires</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>0f3a-9c21</td>
                <td>Operator</td>
                <td>Kitchen panel</td>
                <td>192.0.2.44</td>
                <Text as="td" tone="muted">
                  Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/537.36 Chrome/120
                </Text>
                <td>14:02</td>
                <td>2 min ago</td>
                <td>in 29 days</td>
              </tr>
              <tr>
                <td>b71c-04ee</td>
                <td>Operator</td>
                <td>Laptop</td>
                <td>192.0.2.12</td>
                <Text as="td" tone="muted">
                  Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/605.1.15 Safari/605.1.15
                </Text>
                <td>09:41</td>
                <td>1 hour ago</td>
                <td>in 6 days</td>
              </tr>
            </tbody>
          </Table>
        </Card>
      </GallerySection>

      <GallerySection
        title="Long cell content"
        note="A cell wraps. Columns are sized by the browser from their content, which is why a table with one very long column and five short ones needs the scroll box above rather than a hopeful max-width."
      >
        <Card flush>
          <Table>
            <thead>
              <tr>
                <th>Routine</th>
                <th>What it does</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>Evening</td>
                <td>
                  At sunset, if anyone is home, bring the living room and hallway to 40% over four minutes,
                  close the west blinds, and stop if the wall panel has been touched in the last ten minutes.
                </td>
              </tr>
              <tr>
                <td>Away</td>
                <td>Everything off.</td>
              </tr>
            </tbody>
          </Table>
        </Card>
      </GallerySection>
    </GalleryPage>
  );
}
