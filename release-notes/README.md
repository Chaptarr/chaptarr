# Release-note fragments

Add one Markdown fragment for every user-facing pull request. The release
workflow collects fragments added since the last published release and includes
them in the generated changelog, GitHub release, and Discord announcement.

Use this format:

~~~
---
category: fixed
audience: users, operators
area: metadata
action: none
breaking: false
---
Book searches now keep working from cached metadata during a brief catalog outage.
~~~

The frontmatter records:

- category: added, changed, fixed, security, removed, or deprecated.
- audience: users, operators, or users, operators.
- area: a short lowercase slug, such as bookshelf, metadata, or distribution.
- action: the required upgrade or operating step, or none.
- breaking: true or false. Breaking changes must include an action.

The body must describe the user impact in 30-400 characters, begin with a
capitalized sentence, and end with sentence punctuation. Do not use commit
messages, logs, placeholders, or implementation-only descriptions.

Fragments are append-only after their release. Add a new file rather than
editing a fragment that has shipped. Preview changes with:

~~~
yarn release-notes:preview --base origin/main --head HEAD
~~~

For changes with no user-visible behavior, documentation, security, or
operational effect, put release-note: none in the pull request description and
select the internal-only option in the pull request template.
