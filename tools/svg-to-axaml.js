const fs=require("fs");
const dir="src/Gitfs.App/Assets/icons";
// «mark» — полный знак продукта, «mark-min» — тот же знак без внутренней
// детали, для мелких размеров. Раньше собирался только второй, и знака не было
// ни в одном окне: собрать нечего.
// «drive» и «file» нужны дереву превью: строки там были голым текстом, хотя
// комплект знаков для них есть.
const want=["mark","mark-min","drive","folder","file",
            "view-branches","view-tags","view-commits","view-dates",
            "view-history","view-system","marker-truncated","marker-submodule"];

const num=s=>parseFloat(s);
function rectPath(a){
  const x=num(a.x),y=num(a.y),w=num(a.width),h=num(a.height),r=a.rx?num(a.rx):0;
  if(!r) return `M${x} ${y}H${x+w}V${y+h}H${x}Z`;
  return `M${x+r} ${y}H${x+w-r}A${r} ${r} 0 0 1 ${x+w} ${y+r}`+
         `V${y+h-r}A${r} ${r} 0 0 1 ${x+w-r} ${y+h}`+
         `H${x+r}A${r} ${r} 0 0 1 ${x} ${y+h-r}`+
         `V${y+r}A${r} ${r} 0 0 1 ${x+r} ${y}Z`;
}
function circlePath(a){
  const cx=num(a.cx),cy=num(a.cy),r=num(a.r);
  return `M${cx-r} ${cy}A${r} ${r} 0 1 0 ${cx+r} ${cy}A${r} ${r} 0 1 0 ${cx-r} ${cy}Z`;
}
const attrs=t=>Object.fromEntries([...t.matchAll(/([a-zA-Z-]+)="([^"]*)"/g)].map(m=>[m[1],m[2]]));

const out=[];
for(const name of want){
  const file=`${dir}/${name}.svg`;
  if(!fs.existsSync(file)){ console.error("нет",name); continue; }
  const svg=fs.readFileSync(file,"utf8").replace(/\n/g," ");
  const stroke=[], fill=[];
  for(const m of svg.matchAll(/<(path|rect|circle)\b([^>]*)>/g)){
    const a=attrs(m[2]);
    let d = m[1]==="path" ? a.d : m[1]==="rect" ? rectPath(a) : circlePath(a);
    if(!d) continue;
    (a.fill==="currentColor" ? fill : stroke).push(d);
  }
  out.push({name, stroke:stroke.join(" "), fill:fill.join(" ")});
}

const lines = out.map(i=>{
  let s=`  <StreamGeometry x:Key="Ic${i.name.split(/[-_]/).map(p=>p[0].toUpperCase()+p.slice(1)).join("")}Stroke">${i.stroke}</StreamGeometry>`;
  if(i.fill) s+=`\n  <StreamGeometry x:Key="Ic${i.name.split(/[-_]/).map(p=>p[0].toUpperCase()+p.slice(1)).join("")}Fill">${i.fill}</StreamGeometry>`;
  return s;
}).join("\n");

fs.writeFileSync("src/Gitfs.App/Icons.axaml",
`<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <!-- Комплект знаков дизайн-отдела, переведённый в геометрию Avalonia.
       Исходники — Assets/icons/*.svg, перевод делает tools/svg-to-axaml.js:
       путь остаётся путём, rect и circle разворачиваются в дуги. Никаких
       новых зависимостей: рисовать SVG в Avalonia умеет отдельный пакет, а
       эти знаки — простые обводки, и Path.Data понимает их синтаксис как есть.

       Stroke и Fill разнесены: в исходниках часть кружков залита
       (fill="currentColor"), и рисовать их обводкой значит получить кольцо
       вместо точки. -->

${lines}
</ResourceDictionary>
`);
console.log("иконок:", out.length);
for(const i of out) console.log(" ", i.name, "stroke:", i.stroke.length, "fill:", i.fill.length);
