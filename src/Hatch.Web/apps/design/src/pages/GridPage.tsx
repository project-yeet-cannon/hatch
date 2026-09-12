import { Card, Field, Grid, Text } from '@hatch/ui';
import { GalleryPage, GallerySection } from '../components/Gallery';

/* Drag the window narrow while this page is open. Both grids reflow on their
   own, and that is the thing to look at here — the column count is a minimum
   track width, not a count, so the browser decides how many fit. */

export function GridPage() {
  return (
    <GalleryPage
      title="Grid"
      blurb="The arrangement admin's forms and card decks are laid out in. One gap, stated once — which is what makes two pages built a week apart look like the same product."
    >
      <GallerySection
        title="cols=2"
        note="A minimum track of 400px. For fields that need room to read — an address, a URL, a cron expression."
      >
        <Grid cols={2}>
          <Field label="Calendar URL">
            <input type="text" defaultValue="https://calendar.example/feed.ics" readOnly />
          </Field>
          <Field label="Display name">
            <input type="text" defaultValue="Household" readOnly />
          </Field>
        </Grid>
      </GallerySection>

      <GallerySection
        title="cols=3"
        note="A minimum track of 300px. Admin's default — eight of its twelve grids are this one."
      >
        <Grid cols={3}>
          <Field label="Name">
            <input type="text" defaultValue="Living Room" readOnly />
          </Field>
          <Field label="Kind">
            <select defaultValue="Interior">
              <option>Interior</option>
              <option>Outside</option>
            </select>
          </Field>
          <Field label="Sort order">
            <input type="number" defaultValue={0} readOnly />
          </Field>
        </Grid>
      </GallerySection>

      <GallerySection
        title="It is not a column count"
        note="Both of these are cols=3. The narrow one gets one column, because 300px does not fit twice in 420px. A fixed three-column grid would need a media query on every page that used it."
      >
        <div className="stage stage--narrow">
          <div className="stage-page">
            <Grid cols={3}>
              <Card>
                <Text tone="muted">One</Text>
              </Card>
              <Card>
                <Text tone="muted">Two</Text>
              </Card>
              <Card>
                <Text tone="muted">Three</Text>
              </Card>
            </Grid>
          </div>
        </div>
      </GallerySection>

      <GallerySection
        title="Uneven content"
        note="Cells stretch to the tallest in their row rather than each sizing itself, so a deck reads as rows and not as a skyline."
      >
        <Grid cols={3}>
          <Card>
            <h3>Short</h3>
            <Text tone="muted">One line.</Text>
          </Card>
          <Card>
            <h3>Long</h3>
            <Text tone="muted">
              Four lines of text about what this card is for, so the row has something to stretch to and the
              alignment of the two short ones beside it can be judged against it.
            </Text>
          </Card>
          <Card>
            <h3>Short</h3>
            <Text tone="muted">One line.</Text>
          </Card>
        </Grid>
      </GallerySection>

      <GallerySection title="One cell" note="A grid with one thing in it is a grid with one column. It does not stretch the cell to the full width and it does not centre it.">
        <Grid cols={3}>
          <Card>
            <Text tone="muted">Alone in the row.</Text>
          </Card>
        </Grid>
      </GallerySection>
    </GalleryPage>
  );
}
