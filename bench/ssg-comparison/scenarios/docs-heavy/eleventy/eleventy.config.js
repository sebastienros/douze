// Docs-heavy scenario: Prism through the official syntax highlighting plugin, heading ids from markdown-it-anchor.
import syntaxHighlight from "@11ty/eleventy-plugin-syntaxhighlight";
import markdownItAnchor from "markdown-it-anchor";

export default function (eleventyConfig) {
  eleventyConfig.addPlugin(syntaxHighlight);
  eleventyConfig.amendLibrary("md", (md) => md.use(markdownItAnchor, { level: [2, 3] }));
}

export const config = {
  dir: { input: "content", includes: "../_includes", data: "../_data", output: "_site" },
};
