// Load this configuration that provide all the base for working with conventional commits
const config = require('semantic-release-preconfigured-conventional-commits')

/*
 Commands executed during release.
*/
const publishCommands = `
git tag -a -f \${nextRelease.version} \${nextRelease.version} -F CHANGELOG.md || exit 2
git push --force origin \${nextRelease.version} || exit 3
`
// Only release on branch main
const releaseBranches = ["main"]

config.branches = releaseBranches

config.plugins.push(
    // Custom release commands
    ["@semantic-release/exec", {
        "publishCmd": publishCommands,
    }],
    "@semantic-release/github",
    "@semantic-release/git",
)

// JS Semantic Release configuration must export the JS configuration object
module.exports = config