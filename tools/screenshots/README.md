# Screenshots

Renders every screen of the app from a test card, without opening a window: for design work
and for checking a change on every screen at once, at the normal window size and at the smallest.

```bash
dotnet run --project tools/screenshots -- testdata/cards/three-sites /tmp/shots <models folder or ->
```

The first run imports a copy of the card with a fresh data folder and, with models, waits for the
animal recognition (a few minutes). That state is kept in `<work>/snapshot`, so later runs walk
through all the screens in about a minute. Delete the snapshot after changing the card or the
database. The pictures end up in `<work>/shots`.

The app also watches for real memory cards while it runs: eject cards (and mounted card images)
first, the tool refuses to start otherwise. Test cards are made with `tools/testdata`, models with
`tools/models`.
