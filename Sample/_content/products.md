---
pagination:
  data: products
  size: 2
  alias: thing
products:
 - item1
 - item2
 - item3
 - item4
 - item5
 - item6
 - item7
---
<ol>
{%- for item in thing %}
  <li>{{ item }}</li>
{%- endfor %}
</ol>
