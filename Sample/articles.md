---
title: Articles
---
<!-- Layout: {{ layout }} -->
# Articles

This is the articles index

<div class="articles">
    {%- for a in collections.article %}
    <div class="article">
        <a href="{{ a.data.url }}">{{ a.data.title }}</a>
    </div>
    {%- endfor %}
</div>