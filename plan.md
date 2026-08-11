可以，而且这是设计自研渲染器比较推荐的方式。

你可以先搭好 **SRP 风格的整体架构**，但是每个 Pass 先做最简单实现。这样以后扩展不会推翻架构。

例如：

```text
RenderPipeline
    |
    +-- RenderPass[]
            |
            +-- ShadowPass
            +-- DepthPass
            +-- OpaquePass
            +-- TransparentPass
            +-- PostProcessPass
```

一开始：

```text
ShadowPass
    -> 空实现

DepthPass
    -> 空实现

OpaquePass
    -> 简单 Forward Shader

TransparentPass
    -> 简单 Alpha Blend

PostProcessPass
    -> 直接 Copy
```

先让主流程跑通。

---

例如你的 `RenderPipeline`：

```csharp
public class ForwardPipeline
{
    List<RenderPass> passes;

    public void Render(Camera camera)
    {
        foreach(var pass in passes)
        {
            pass.Execute(camera);
        }
    }
}
```

以后增加功能：

原来：

```csharp
class ShadowPass
{
    Execute()
    {
        return;
    }
}
```

变成：

```csharp
class ShadowPass
{
    Execute()
    {
        RenderShadowMap();
    }
}
```

其他 Pass 不需要修改。

---

## 初期 Pass 可以这样设计

### 1. ShadowPass

第一版：

不做阴影：

```text
ShadowTexture = white
```

Shader：

```glsl
shadow = 1.0;
```

所有地方都亮。

以后：

加入：

* Directional Light
* Shadow Map
* PCF

---

### 2. DepthPass

第一版：

可以没有。

需要时：

加：

```text
DepthTexture
```

支持：

* SSAO
* Fog
* Outline
* TAA

---

### 3. OpaquePass

这个是核心。

第一版：

直接 Forward：

```text
Mesh
 |
Material
 |
Shader
 |
Framebuffer
```

支持：

* Mesh
* Material
* Directional Light

就可以看到世界。

---

### 4. TransparentPass

第一版：

支持：

```text
alpha blending
```

例如：

* 粒子
* UI
* 玻璃

---

### 5. PostProcessPass

第一版：

直接：

```text
Input Texture
      |
      v
Output Texture
```

甚至什么都不处理。

以后加：

* Bloom
* Tone Mapping
* FXAA

---

## 重要的是提前定义接口

不要：

```csharp
Render()
{
    DrawObjects();
    DrawShadow();
    DrawUI();
}
```

因为以后很难扩展。

应该：

```csharp
interface IRenderPass
{
    void Setup(RenderContext context);
    void Execute(RenderContext context);
    void Cleanup();
}
```

---

## 还可以把 Pass 做成配置

例如：

```yaml
Pipeline:
  - ShadowPass
  - ForwardPass
  - TransparentPass
  - UIPass
```

以后：

卡通渲染：

```yaml
Pipeline:
  - DepthPass
  - ToonPass
  - OutlinePass
```

写实：

```yaml
Pipeline:
  - ShadowPass
  - GBufferPass
  - LightingPass
  - SSRPass
```

---

这也是 Unity SRP 的核心思想：

不是“URP/HDRP里面有固定几个 Pass”，而是：

> Pipeline 决定有哪些 Pass，以及 Pass 如何组合。

所以你的引擎完全可以：

第一阶段：

```
SRP架构
+
Forward Renderer
+
少量Shader
```

先完成。

以后逐步替换 Pass，而不是重写渲染器。对于自研引擎，这比一开始实现完整 PBR、Deferred、各种后处理更合理。
