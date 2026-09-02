import { Button, Card, PageHeader, Text } from '@aerie/ui';
import { GalleryPage, GallerySection } from '../components/Gallery';

/* The headers on this page render <h1>. So does the gallery page around them,
   which means this one page has several — the one context where that is
   correct, because the specimens are what the page is about. */

export function PageHeaderPage() {
  return (
    <GalleryPage
      title="Page header"
      blurb="The top of a page, and the component that owns the page's <h1>. That is the whole reason it exists at this size."
    >
      <GallerySection
        title="Why it owns the h1"
        note="The top bar's app name is a wordmark in the banner landmark, not a page heading — so admin's pages were left titling themselves with <h2> and no <h1> anywhere above them. Every page rendering this closes that, and doing it in one component made the lift one edit rather than twelve. The size step from --t-heading to --t-title is the one visible type change the primitives phase makes."
      >
        <Card>
          <PageHeader title="Zones" actions={<Button variant="primary">Add zone</Button>} />
          <Text tone="muted">The page starts here.</Text>
        </Card>
      </GallerySection>

      <GallerySection
        title="Title only"
        note="No actions, no rule under it, no repeat of the app name. The bar above says where you are; this says what is on the page."
      >
        <Card>
          <PageHeader title="Settings" />
        </Card>
      </GallerySection>

      <GallerySection
        title="With a description"
        note="One line saying what the page is for. When it is present the header aligns to the top instead of centring — a one-line button centred against a two-line block hangs in the middle of the paragraph."
      >
        <Card>
          <PageHeader
            title="Revisions"
            description="Which commit each part of the system is running. Refreshes every 15s."
            actions={<Button>Refresh</Button>}
          />
        </Card>
      </GallerySection>

      <GallerySection
        title="Several actions"
        note="They keep their size while the title gives way — the same rule the top bar states. A target that shrinks to make room for a title is the wrong thing to shrink."
      >
        <Card>
          <PageHeader
            title="People"
            actions={
              <>
                <Button>Import</Button>
                <Button variant="primary">Add person</Button>
              </>
            }
          />
        </Card>
      </GallerySection>

      <GallerySection
        title="level=2, for a section within a page"
        note="A page has one h1, and admin's Revisions page opens three sections under it. Same shape, correct outline — rather than a second component that would have to be kept looking like this one."
      >
        <Card>
          <PageHeader
            title="Cluster"
            level={2}
            description="What Flux has fetched, and what each Kustomization has actually applied from it."
          />
        </Card>
      </GallerySection>

      <GallerySection
        title="A long title, in a narrow column"
        note="The title wraps. It does not ellipse — a page whose name you cannot read is a page you cannot tell anyone you are on. The bar above ellipses because it must fit one row; a page header has the page."
      >
        <div className="stage stage--narrow">
          <div className="stage-page">
            <PageHeader
              title="Provisioning and panel enrolment"
              actions={<Button variant="primary">New token</Button>}
            />
          </div>
        </div>
      </GallerySection>
    </GalleryPage>
  );
}
