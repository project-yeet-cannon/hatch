import { Badge, Button, Card, Grid, Table, Text } from '@aerie/ui';
import { GalleryPage, GallerySection } from '../components/Gallery';

export function CardPage() {
  return (
    <GalleryPage
      title="Card"
      blurb="The surface everything else sits on: --card, a hairline, the card radius, one rung of padding and the single elevation step."
    >
      <GallerySection
        title="A card"
        note="It states no layout for its children. A card that arranged its own contents would be a surface and a stack welded together, and every site wanting the surface with a different arrangement inside would be fighting it."
      >
        <Card>
          <h3>Kitchen panel</h3>
          <Text tone="muted">Wall tablet, 10 inch. Sleeps at 22:30.</Text>
        </Card>
      </GallerySection>

      <GallerySection
        title="Flush"
        note="A card that gives up its padding for content running to its own edges. The frame stays, and the corners clip — which is what keeps a full-width table from squaring off the card's radius."
      >
        <Card flush>
          <Table>
            <thead>
              <tr>
                <th>Zone</th>
                <th>Kind</th>
                <th>Included</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>Living Room</td>
                <td>Interior</td>
                <td>
                  <Badge tone="success">Yes</Badge>
                </td>
              </tr>
              <tr>
                <td>Back Porch</td>
                <td>Outside</td>
                <td>
                  <Badge>No</Badge>
                </td>
              </tr>
            </tbody>
          </Table>
        </Card>
      </GallerySection>

      <GallerySection
        title="A deck"
        note="Cards in a <Grid>. They stretch to the row's height rather than each sizing itself, so a row of cards reads as a row and not as a skyline."
      >
        <Grid cols={3}>
          <Card>
            <h3>Zones</h3>
            <Text tone="muted">6 configured, 5 shown on the wall.</Text>
          </Card>
          <Card>
            <h3>Routines</h3>
            <Text tone="muted">
              14 configured. Two of them run only between sunset and 23:00, which is a longer sentence than
              its neighbours have, so this card is the tall one.
            </Text>
          </Card>
          <Card>
            <h3>Panels</h3>
            <Text tone="muted">3 paired.</Text>
          </Card>
        </Grid>
      </GallerySection>

      <GallerySection
        title="Empty, and nearly empty"
        note="A card with nothing in it still draws — it is a surface, and the rule about rendering nothing belongs to whatever was going to fill it, not to the box. That is what <EmptyState> is for."
      >
        <Grid cols={2}>
          <Card />
          <Card>
            <div className="row">
              <Text tone="muted" as="span">
                Nothing paired yet.
              </Text>
              <Button variant="primary">Pair a panel</Button>
            </div>
          </Card>
        </Grid>
      </GallerySection>

      <GallerySection
        title="On both grounds"
        note="A card is --card on --bg. In dark that is #2d2d2d on #1a1a1a, a step of about 5% lightness plus the hairline — check both here, because a card that vanishes into the page in one theme is the failure this pairing has."
      >
        <div className="ground-row">
          <div className="ground">
            <Card>
              <h3>On the page ground</h3>
              <Text tone="muted">--card on --bg, as admin renders it.</Text>
            </Card>
          </div>
        </div>
      </GallerySection>
    </GalleryPage>
  );
}
