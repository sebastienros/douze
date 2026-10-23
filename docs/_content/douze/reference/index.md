---
title: Reference
description: The exhaustive reference of Douze, its command line, douze.yml, template data, Liquid and built-in steps.
---

The reference lists every command, option, setting, value, filter and step of Douze, with its default and the
errors it reports. Each page follows the code of the current `main`. For explanations
and walkthroughs, start with the [guide](../guide/workflow-architecture.md) or the [tutorial](../tutorial/index.md).

## Pages

| Page | Contains |
|---|---|
| [Command line](cli.md) | `build`, `watch`, `serve`, `doctor`, `tools install`, `worker-helper`; every option, exit codes and environment variables. |
| [douze.yml](configuration.md) | The `site`, `markdown` and `steps` sections, globs, durations, the default workflow and validation errors. |
| [Template data](template-data.md) | `page`, `url`, `content`, `collections`, `pagination`, `site`, `headings`, `toc`, locale and image values, and how names resolve. |
| [Liquid filters and tags](liquid.md) | Every filter and tag Douze adds to Liquid, and shortcodes. |
| [Command steps and workers](commands.md) | Steps that run external commands: modes, fields, template values, environment, the worker protocol and helper. |
| [Built-in steps](steps/index.md) | Every built-in step, with its options. |

## Built-in steps

| Group | Steps |
|---|---|
| Data | [data](steps/data.md), [frontmatter](steps/frontmatter.md), [parse](steps/parse.md) |
| Paths and lists | [permalink](steps/permalink.md), [collect](steps/collect.md), [paginate](steps/paginate.md), [localize](steps/localize.md), [fork](steps/fork.md) |
| Rendering | [replace](steps/replace.md), [liquid](steps/liquid.md), [markdown](steps/markdown.md), [headings](steps/headings.md), [layout](steps/layout.md) |
| Code | [highlight](steps/highlight.md), [hover-typescript](steps/hover-typescript.md), [hover-csharp](steps/hover-csharp.md), [hover-go](steps/hover-go.md), [check-code](steps/check-code.md) |
| Images | [images](steps/images.md), [image-map](steps/image-map.md), [pictures](steps/pictures.md), [social-image](steps/social-image.md) |
| Output | [write](steps/write.md) |
