#define GL_GLEXT_PROTOTYPES
#include <GL/osmesa.h>
#include <GL/glext.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

static GLuint shader(GLenum stage, const char *source) {
    GLuint id = glCreateShader(stage);
    glShaderSource(id, 1, &source, NULL);
    glCompileShader(id);
    GLint ok; glGetShaderiv(id, GL_COMPILE_STATUS, &ok);
    if (!ok) { char log[8192]; glGetShaderInfoLog(id, sizeof(log), NULL, log); fprintf(stderr, "%s\n", log); exit(1); }
    return id;
}

int main(void) {
    int attributes[] = {OSMESA_FORMAT, OSMESA_RGBA, OSMESA_PROFILE, OSMESA_CORE_PROFILE,
        OSMESA_CONTEXT_MAJOR_VERSION, 4, OSMESA_CONTEXT_MINOR_VERSION, 6, 0};
    OSMesaContext context = OSMesaCreateContextAttribs(attributes, NULL);
    unsigned char pixels[16 * 16 * 4] = {0};
    if (!context || !OSMesaMakeCurrent(context, pixels, GL_UNSIGNED_BYTE, 16, 16)) return 1;
    printf("Renderer: %s, OpenGL: %s\n", glGetString(GL_RENDERER), glGetString(GL_VERSION));

#define LOAD(type, name) type name = (void *)OSMesaGetProcAddress(#name)
    LOAD(PFNGLCREATETEXTURESPROC, glCreateTextures);
    LOAD(PFNGLTEXTURESTORAGE2DPROC, glTextureStorage2D);
    LOAD(PFNGLTEXTURESUBIMAGE2DPROC, glTextureSubImage2D);
    LOAD(PFNGLTEXTUREPARAMETERIPROC, glTextureParameteri);
    LOAD(PFNGLCREATEBUFFERSPROC, glCreateBuffers);
    LOAD(PFNGLNAMEDBUFFERDATAPROC, glNamedBufferData);
    LOAD(PFNGLCREATEVERTEXARRAYSPROC, glCreateVertexArrays);

    PFNGLGETTEXTUREHANDLEARBPROC handle = (void *)OSMesaGetProcAddress("glGetTextureHandleARB");
    PFNGLMAKETEXTUREHANDLERESIDENTARBPROC resident = (void *)OSMesaGetProcAddress("glMakeTextureHandleResidentARB");
    PFNGLMAKETEXTUREHANDLENONRESIDENTARBPROC nonresident = (void *)OSMesaGetProcAddress("glMakeTextureHandleNonResidentARB");
    GLuint texture; glCreateTextures(GL_TEXTURE_2D, 1, &texture);
    glTextureStorage2D(texture, 1, GL_RGBA8, 1, 1);
    unsigned char color[] = {37, 149, 231, 255};
    glTextureSubImage2D(texture, 0, 0, 0, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, color);
    glTextureParameteri(texture, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    GLuint64 address = handle(texture); resident(address);
    if (!address || glGetError()) { fprintf(stderr, "Bindless handle creation failed\n"); return 1; }

    GLuint buffer; glCreateBuffers(1, &buffer);
    glNamedBufferData(buffer, sizeof(address), &address, GL_STATIC_DRAW);
    glBindBufferBase(GL_SHADER_STORAGE_BUFFER, 0, buffer);

    GLuint program = glCreateProgram();
    glAttachShader(program, shader(GL_VERTEX_SHADER,
        "#version 460\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.-1.,0,1);}"));
    glAttachShader(program, shader(GL_FRAGMENT_SHADER,
        "#version 460\n#extension GL_ARB_bindless_texture : require\n"
        "layout(std430,binding=0) readonly buffer Data{sampler2D tex;};out vec4 color;"
        "void main(){color=texture(tex,vec2(.5));}"));
    glLinkProgram(program);
    GLint ok; glGetProgramiv(program, GL_LINK_STATUS, &ok);
    if (!ok) { char log[8192]; glGetProgramInfoLog(program,sizeof(log),NULL,log); fprintf(stderr,"%s\n",log); return 1; }
    glUseProgram(program);
    GLuint vao; glCreateVertexArrays(1, &vao); glBindVertexArray(vao);
    glViewport(0, 0, 16, 16); glDrawArrays(GL_TRIANGLES, 0, 3); glFinish();
    unsigned char result[4]; glReadPixels(8, 8, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, result);
    if (glGetError() || abs(result[0]-37)>1 || abs(result[1]-149)>1 || abs(result[2]-231)>1) {
        fprintf(stderr,"Bindless sampling failed: %u %u %u\n",result[0],result[1],result[2]); return 1;
    }
    nonresident(address); glDeleteTextures(1, &texture); glDeleteBuffers(1, &buffer);
    OSMesaDestroyContext(context);
    puts("PASS: SSBO bindless texture rendered the expected pixels.");
    return 0;
}
