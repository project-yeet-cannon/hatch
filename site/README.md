# The Hatch site

The GitHub Pages site for Hatch: a quick start, a guide to working the board,
and the reference for the runner. Built from this directory by
`.github/workflows/pages.yml` on every push to the default branch.

Preview locally:

```
cd site
bundle install
bundle exec jekyll serve
```

Then open the address Jekyll prints. Repository links on the site come from
`site.github.*`, which Jekyll fills in from the environment on GitHub and leaves
mostly empty locally; that is expected.
