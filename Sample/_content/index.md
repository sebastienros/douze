---
name: Bill
colors:
- name: Red
  value: 1  
- name: <Blue>
  value: 2  
title: Home
---
<!-- Layout: {{ layout }} -->

# Home Page

This is the home page for {{ name }} !!!
{% for c in colors %} {{c.name}}: {{c.value}} {% endfor %}
{% for i in (1..11) %} {{ i }} {% endfor %}

characters: {{ characters.size }}
colors: {{ colors.size }}

{%- capture footer %}
<!-- FOOTER -->
<strong>hello</strong>
{%- endcapture %}