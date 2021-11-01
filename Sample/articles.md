---
title: Articles
---
<!-- Layout: {{ layout }} -->
# Articles

This is the articles index

<div class="articles">
    {% for a in collections.article %}
    <div class="article">
        {{ a.url }}
        {{ a.data.url }}
    </div>
    {% endfor %}
</div>